#include "pch.h"

#include <atomic>
#include <cstdlib>
#include <limits>
#include <map>
#include <mutex>
#include <set>
#include <shared_mutex>
#include <vector>
#include <android/log.h>
#include <sys/system_properties.h>
#include <vulkan/vulkan.h>
#include <IUnityGraphicsVulkan.h>
#include <api/make_ref_counted.h>
#include <api/video/video_frame.h>
#include <api/video_codecs/sdp_video_format.h>
#include <api/video_codecs/video_decoder.h>
#include <modules/video_coding/include/video_error_codes.h>
#include "AhbConvertPass.h"
#include "AhbDecodePipeline.h"
#include "AhbDisplayBuffer.h"
#include "AhbH264DecoderFactory.h"
#include "AhbMediaCodec.h"
#include "AhbVulkanContext.h"
#include "Android/AndroidCodecFactoryHelper.h"
#include "GraphicsDevice/Vulkan/LoadVulkanFunctions.h"
#include "ReceivePathTelemetry.h"

#define AHB_LOG(...) __android_log_print(ANDROID_LOG_INFO, "AhbH264", __VA_ARGS__)

namespace unity
{
namespace webrtc
{
    namespace
    {
        class AhbH264Decoder;
        // Decode/import may run concurrently with rendering, but never device destruction.
        // Lock order is device gate, then convert mutex.
        std::shared_mutex g_deviceMutex;
        bool g_deviceOpen = false;
        std::mutex g_convertMutex;
        std::map<uint64_t, AhbH264Decoder*> g_liveDecoders;
        std::map<uint32_t, uint64_t> g_rendererToDecoder;
        std::atomic<uint64_t> g_nextDecoderId { 1 };

        struct RetireCtx { AImage* image; };
        void RetireInput(void* p)
        {
            auto* ctx = static_cast<RetireCtx*>(p);
            AhbMediaCodec::ReleaseImage(ctx->image);
            delete ctx;
        }

        struct Pending
        {
            AhbFrameImage frame {};
            AImage* image = nullptr;
            bool valid = false;
        };

        // A codec output reader, imported images and descriptor sets share one lifetime.
        // Once retired, only the render safe-frame drain can destroy the generation.
        struct Generation
        {
            VkDevice device = VK_NULL_HANDLE;
            AhbMediaCodec codec;
            AhbVkImporter importer;
            AhbConvertPass convert;
            Pending pending;
            int configuredWidth = 0;
            int configuredHeight = 0;

            void DropPending()
            {
                if (pending.valid)
                    AhbMediaCodec::ReleaseImage(pending.image);
                pending = Pending {};
            }
            ~Generation()
            {
                RTC_DCHECK(!convert.HasInFlight());
                DropPending();
                convert.Free(false);
                importer.Shutdown();
                codec.Release(); // readers are deleted only after every GPU consumer retired
            }
        };
        std::vector<std::unique_ptr<Generation>> g_retired;

        void RetireLocked(std::unique_ptr<Generation> generation)
        {
            if (!generation)
                return;
            generation->DropPending(); // not recorded into any GPU command buffer
            if (generation->convert.HasInFlight())
                g_retired.push_back(std::move(generation));
            // A generation without recorded GPU work can be destroyed immediately.
        }
        void ReclaimLocked(uint64_t safeFrame)
        {
            for (auto it = g_retired.begin(); it != g_retired.end();)
            {
                (*it)->convert.Reclaim(safeFrame);
                if (!(*it)->convert.HasInFlight())
                    it = g_retired.erase(it);
                else
                    ++it;
            }
        }
        int ReadAhbMode()
        {
            char buf[PROP_VALUE_MAX] = { 0 };
            return __system_property_get("debug.ahb.mode", buf) > 0 ? atoi(buf) : 0;
        }

        class AhbH264Decoder : public ::webrtc::VideoDecoder
        {
        public:
            AhbH264Decoder(std::unique_ptr<::webrtc::VideoDecoder> inner, std::string mime)
                : inner_(std::move(inner)), mime_(std::move(mime)), id_(g_nextDecoderId.fetch_add(1)) {}
            ~AhbH264Decoder() override { Release(); }

            bool Configure(const Settings& settings) override
            {
                NativeRelease();
                if (delegateConfigured_ && inner_)
                    inner_->Release();
                delegateConfigured_ = false;
                mode_ = ReadAhbMode();
                if (mode_ < 2)
                {
                    bool ok = inner_ && inner_->Configure(settings);
                    delegateConfigured_ = ok;
                    if (ok && realCallback_)
                        inner_->RegisterDecodeCompleteCallback(realCallback_);
                    AHB_LOG("Configure delegate mode=%d ok=%d", mode_, ok);
                    return ok;
                }
                std::shared_lock<std::shared_mutex> deviceLock(g_deviceMutex);
                const UnityVulkanInstance* vk = g_deviceOpen ? GetUnityVulkanInstance() : nullptr;
                AhbVkImporter probe;
                if (!vk || !probe.Init(vk->device))
                {
                    AHB_LOG("Configure rejected: opt-in AHB requires initialized Vulkan + AHB/ycbcr support");
                    return false;
                }
                m_keyFrameRequired = true;
                AHB_LOG("Configure native opt-in mode=%d id=%llu", mode_, static_cast<unsigned long long>(id_));
                return true;
            }
            int32_t Decode(const ::webrtc::EncodedImage& input, bool missingFrames, int64_t renderTimeMs) override
            {
                if (mode_ < 2)
                    return inner_ ? inner_->Decode(input, missingFrames, renderTimeMs) : WEBRTC_VIDEO_CODEC_ERROR;
                if (m_keyFrameRequired)
                {
                    if (input._frameType != ::webrtc::VideoFrameType::kVideoFrameKey)
                        return WEBRTC_VIDEO_CODEC_NO_OUTPUT;
                    m_keyFrameRequired = false;
                }
                if (!NativeDecode(input))
                {
                    NativeRelease(); // retires the generation, never deletes an in-flight reader
                    m_keyFrameRequired = true;
                    return WEBRTC_VIDEO_CODEC_ERROR;
                }
                return WEBRTC_VIDEO_CODEC_OK;
            }
            int32_t RegisterDecodeCompleteCallback(::webrtc::DecodedImageCallback* callback) override
            {
                realCallback_ = callback;
                if (mode_ < 2 && inner_)
                    return inner_->RegisterDecodeCompleteCallback(callback);
                return WEBRTC_VIDEO_CODEC_OK;
            }
            int32_t Release() override
            {
                NativeRelease();
                if (delegateConfigured_ && inner_)
                {
                    delegateConfigured_ = false;
                    return inner_->Release();
                }
                return WEBRTC_VIDEO_CODEC_OK;
            }
            DecoderInfo GetDecoderInfo() const override
            {
                if (mode_ < 2 && inner_)
                    return inner_->GetDecoderInfo();
                DecoderInfo info;
                info.implementation_name = "AhbH264-zerocopy-prototype";
                info.is_hardware_accelerated = true;
                return info;
            }

            // Called under device shared lock + convert mutex.
            void ConvertPendingInto(VkCommandBuffer cmd, VkImage dstImage, uint64_t curFrame, uint64_t safeFrame)
            {
                if (!generation_)
                    return;
                Generation& gen = *generation_;
                gen.convert.Reclaim(safeFrame);
                if (!gen.pending.valid)
                {
                    // Preserve Unity's existing texture. Repainting an imported buffer after
                    // returning its AImage allows MediaCodec to overwrite it during sampling.
                    RecordReceivePath(ReceivePathCounter::Skipped);
                    return;
                }
                if (!gen.convert.IsReady())
                {
                    const UnityVulkanInstance* vk = GetUnityVulkanInstance();
                    if (!vk || !gen.convert.Init(vk->device, vk->physicalDevice, vk->queueFamilyIndex,
                            vk->graphicsQueue, gen.importer.YcbcrSampler()))
                    {
                        RecordReceivePath(ReceivePathCounter::ImportFailures);
                        gen.DropPending();
                        return;
                    }
                }
                const Pending pending = gen.pending;
                gen.pending = Pending {};
                auto* ctx = new RetireCtx { pending.image };
                if (!gen.convert.RecordInto(cmd, pending.frame.image, pending.frame.view, dstImage,
                        pending.frame.width, pending.frame.height, curFrame, &RetireInput, ctx))
                {
                    delete ctx;
                    AhbMediaCodec::ReleaseImage(pending.image);
                    RecordReceivePath(ReceivePathCounter::Skipped);
                }
            }
            void Reclaim(uint64_t safeFrame)
            {
                if (generation_)
                    generation_->convert.Reclaim(safeFrame);
            }
            void RetireForShutdown()
            {
                RetireLocked(std::move(generation_));
                m_keyFrameRequired = true;
            }

        private:
            bool NativeDecode(const ::webrtc::EncodedImage& input)
            {
                std::shared_lock<std::shared_mutex> deviceLock(g_deviceMutex);
                const UnityVulkanInstance* vk = g_deviceOpen ? GetUnityVulkanInstance() : nullptr;
                if (!vk)
                    return false;
                int w = static_cast<int>(input._encodedWidth), h = static_cast<int>(input._encodedHeight);
                const bool haveInputSize = w > 0 && h > 0;
                if (!haveInputSize)
                {
                    w = knownWidth_ > 0 ? knownWidth_ : 1280;
                    h = knownHeight_ > 0 ? knownHeight_ : 720;
                }
                if (!generation_ || (haveInputSize && (w != generation_->configuredWidth || h != generation_->configuredHeight)))
                {
                    auto next = std::make_unique<Generation>();
                    next->device = vk->device;
                    next->configuredWidth = w;
                    next->configuredHeight = h;
                    if (!next->importer.Init(vk->device) || !next->codec.Configure(w, h, mime_.c_str()))
                        return false;
                    std::lock_guard<std::mutex> lock(g_convertMutex);
                    RetireLocked(std::move(generation_));
                    generation_ = std::move(next);
                }
                Generation& gen = *generation_;
                const int64_t ptsUs = static_cast<int64_t>(input.Timestamp()) * 1000000 / 90000;
                if (!gen.codec.DecodeNal(input.data(), input.size(), ptsUs))
                    return false;
                knownWidth_ = gen.codec.Width();
                knownHeight_ = gen.codec.Height();
                AImage* image = nullptr;
                AHardwareBuffer* ahb = gen.codec.AcquireLatest(&image);
                if (!ahb)
                    return true;
                const int dw = haveInputSize ? w : knownWidth_;
                const int dh = haveInputSize ? h : knownHeight_;
                {
                    std::lock_guard<std::mutex> lock(g_convertMutex);
                    AhbFrameImage frame {};
                    if (!gen.importer.EnsureConversion(ahb) || !gen.importer.ImportImage(ahb, dw, dh, &frame))
                    {
                        AhbMediaCodec::ReleaseImage(image);
                        RecordReceivePath(ReceivePathCounter::ImportFailures);
                        return false;
                    }
                    gen.DropPending();
                    gen.pending.frame = frame;
                    gen.pending.image = image;
                    gen.pending.valid = true;
                    g_liveDecoders[id_] = this;
                }
                if (realCallback_)
                {
                    auto buffer = rtc::make_ref_counted<AhbDisplayBuffer>(id_, dw, dh);
                    ::webrtc::VideoFrame frame = ::webrtc::VideoFrame::Builder()
                        .set_video_frame_buffer(buffer).set_timestamp_rtp(input.Timestamp())
                        .set_timestamp_us(ptsUs).build();
                    RecordReceivePath(ReceivePathCounter::AhbFrames);
                    realCallback_->Decoded(frame);
                }
                return true;
            }
            void NativeRelease()
            {
                std::shared_lock<std::shared_mutex> deviceLock(g_deviceMutex);
                std::lock_guard<std::mutex> lock(g_convertMutex);
                g_liveDecoders.erase(id_);
                for (auto it = g_rendererToDecoder.begin(); it != g_rendererToDecoder.end();)
                    if (it->second == id_) it = g_rendererToDecoder.erase(it); else ++it;
                RetireLocked(std::move(generation_));
                m_keyFrameRequired = true;
            }
            std::unique_ptr<::webrtc::VideoDecoder> inner_;
            std::string mime_;
            const uint64_t id_;
            ::webrtc::DecodedImageCallback* realCallback_ = nullptr;
            std::unique_ptr<Generation> generation_;
            bool delegateConfigured_ = false;
            std::atomic<bool> m_keyFrameRequired { true };
            int knownWidth_ = 0;
            int knownHeight_ = 0;
            int mode_ = 0;
        };

        class AhbH264DecoderFactory : public ::webrtc::VideoDecoderFactory
        {
        public:
            AhbH264DecoderFactory()
                : android_(CreateAndroidDecoderFactory())
            {
                AHB_LOG("AhbH264DecoderFactory created (android delegate=%p)", static_cast<void*>(android_.get()));
            }

            std::vector<::webrtc::SdpVideoFormat> GetSupportedFormats() const override
            {
                // Start from what the platform HW decoder factory advertises (H264 Constrained Baseline,
                // VP9, AV1). We do NOT add VP8: the Quest has no HW VP8 decoder, and routing it through our
                // AHB MediaCodec produces a broken chroma plane (green). VP8 is left to libwebrtc's internal
                // libvpx decoder (kInternalImpl) + the standard (non-zero-copy) Texture2D upload — its I420
                // output can't go through the AHB zero-copy compute display anyway. The per-track display
                // switch (a Texture2D when the decoder isn't delivering native AHB frames) is what makes that
                // work. (H265 is also absent: this libwebrtc build has no HEVC codec support at all.)
                auto formats = android_ ? android_->GetSupportedFormats() : std::vector<::webrtc::SdpVideoFormat>();

                // Add H264 High + Constrained High. The platform Java factory (HardwareVideoDecoderFactory)
                // only marks H264 High-capable when the decoder NAME starts with "OMX.qcom."/"OMX.Exynos." —
                // a legacy check that misses the Quest's Codec2 decoder "c2.qti.avc.decoder", so it
                // advertises Constrained Baseline (42e01f) ONLY. But the Quest's HW AVC decoder DOES decode
                // High / Constrained High / Main (verified on-device: MediaCodecList reports
                // c2.qti.avc.decoder hardwareAccelerated, profiles Baseline/CB/Main/High/ConstrainedHigh to
                // Level 6.2). Without these advertised, a browser HARDWARE H.264 encoder — e.g. Chrome/macOS
                // VideoToolbox, which emits High/Constrained-High — negotiates to nothing here and shows no
                // video on Quest. The AHB pipeline is profile-agnostic (it maps "H264" -> "video/avc" and
                // the MediaCodec reads the real profile from the SPS), and level-asymmetry-allowed=1 keeps
                // the advertised level (5.2) from capping a sender that runs higher.
                for (const char* plid : { "640c34" /*Constrained High, 5.2*/, "640034" /*High, 5.2*/ })
                {
                    ::webrtc::SdpVideoFormat f(
                        "H264",
                        { { "profile-level-id", plid },
                          { "level-asymmetry-allowed", "1" },
                          { "packetization-mode", "1" } });
                    bool present = false;
                    for (const auto& g : formats)
                        if (g.IsSameCodec(f)) { present = true; break; }
                    if (present)
                        continue;
                    AHB_LOG("GetSupportedFormats: + H264 %s (Quest HW decodes it; platform factory omitted it)", plid);
                    formats.push_back(f);
                }
                return formats;
            }

            std::unique_ptr<::webrtc::VideoDecoder>
            CreateVideoDecoder(const ::webrtc::SdpVideoFormat& format) override
            {
                if (!android_)
                    return nullptr;
                // Drive the zero-copy AHB pipeline (MediaCodec -> AImageReader -> Vulkan compute) for any
                // codec we can map to a MediaCodec mime. The AHB output path is codec-agnostic, so VP8/VP9/
                // HEVC/AV1 zero-copy exactly like H.264 — and, crucially, they produce the kNative
                // AhbDisplayBuffer the receive-display path needs. A stock-decoder I420 frame would be
                // skipped by the zero-copy display branch and render TRANSPARENT (the VP9 bug).
                const char* mime = MimeForCodec(format.name);
                if (mime)
                {
                    AHB_LOG("CreateVideoDecoder(%s) -> AHB zero-copy (mime=%s)", format.name.c_str(), mime);
                    return std::make_unique<AhbH264Decoder>(android_->CreateVideoDecoder(format), mime);
                }
                // No AHB mime for this codec — hand it to the stock platform decoder (won't zero-copy
                // display, but better than nothing for an unforeseen codec).
                AHB_LOG("CreateVideoDecoder(%s) -> stock platform decoder (no AHB mime)", format.name.c_str());
                return android_->CreateVideoDecoder(format);
            }

        private:
            // SDP codec name -> Android MediaCodec mime for the codecs we drive through the zero-copy AHB
            // pipeline, or nullptr otherwise. Only the codecs the wrapped HW factory advertises reach here
            // (GetSupportedFormats), so each mapped mime is one the device's MediaCodec actually supports.
            // VP8 is intentionally absent: no HW VP8 on Quest, so it stays on libvpx + the standard upload.
            static const char* MimeForCodec(const std::string& name)
            {
                if (name == "H264") return "video/avc";
                if (name == "VP9")  return "video/x-vnd.on2.vp9";
                if (name == "AV1")  return "video/av01";
                return nullptr;
            }

            std::unique_ptr<::webrtc::VideoDecoderFactory> android_;
        };

    } // namespace

    std::unique_ptr<VideoDecoderFactory> CreateAhbH264DecoderFactory()
    {
        return std::make_unique<AhbH264DecoderFactory>();
    }

    void AhbMapRenderer(unsigned int rendererId, unsigned long long decoderId)
    {
        std::lock_guard<std::mutex> lock(g_convertMutex);
        g_rendererToDecoder[rendererId] = decoderId;
    }
    void AhbSetStreamVisible(unsigned int, int) {}

    void AhbInitializeForDevice()
    {
        std::unique_lock<std::shared_mutex> deviceLock(g_deviceMutex);
        g_deviceOpen = true;
    }
    void AhbRetireCompleted(unsigned long long safeFrame)
    {
        std::shared_lock<std::shared_mutex> deviceLock(g_deviceMutex);
        if (!g_deviceOpen) return;
        std::lock_guard<std::mutex> lock(g_convertMutex);
        for (auto& entry : g_liveDecoders)
            entry.second->Reclaim(safeFrame);
        ReclaimLocked(safeFrame);
    }
    void AhbShutdownForDevice()
    {
        std::unique_lock<std::shared_mutex> deviceLock(g_deviceMutex);
        std::lock_guard<std::mutex> lock(g_convertMutex);
        g_deviceOpen = false;
        for (auto& entry : g_liveDecoders)
            entry.second->RetireForShutdown();
        std::set<VkDevice> devices;
        for (auto& generation : g_retired)
            devices.insert(generation->device);
        for (VkDevice device : devices)
            vkDeviceWaitIdle(device);
        ReclaimLocked(std::numeric_limits<uint64_t>::max());
        g_rendererToDecoder.clear();
    }
    bool AhbConvertDecoderInto(unsigned long long id, void* cmd, void* dstImage,
        unsigned long long curFrame, unsigned long long safeFrame)
    {
        std::shared_lock<std::shared_mutex> deviceLock(g_deviceMutex);
        if (!g_deviceOpen) return false;
        std::lock_guard<std::mutex> lock(g_convertMutex);
        ReclaimLocked(safeFrame);
        auto it = g_liveDecoders.find(id);
        if (it == g_liveDecoders.end()) return false;
        it->second->ConvertPendingInto(reinterpret_cast<VkCommandBuffer>(cmd),
            reinterpret_cast<VkImage>(dstImage), curFrame, safeFrame);
        return true;
    }
} // namespace webrtc
} // namespace unity
