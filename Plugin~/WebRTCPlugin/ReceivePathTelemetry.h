#pragma once

#include <atomic>
#include <cstdint>

namespace unity {
namespace webrtc {
// Prototype-only counters for receive-path evidence. Sender readbacks are excluded.
enum class ReceivePathCounter : int {
    AhbFrames = 0,
    GpuWrites = 1,
    CpuConversions = 2,
    CpuUploads = 3,
    ImportFailures = 4,
    Skipped = 5,
    Count = 6
};
inline std::atomic<uint64_t> g_receivePathCounters[6] {};
inline void RecordReceivePath(ReceivePathCounter counter) {
    g_receivePathCounters[static_cast<int>(counter)].fetch_add(1, std::memory_order_relaxed);
}
inline uint64_t ReadReceivePath(int counter) {
    return counter >= 0 && counter < 6 ? g_receivePathCounters[counter].load(std::memory_order_relaxed) : 0;
}
inline void ResetReceivePath() {
    for (auto& counter : g_receivePathCounters) counter.store(0, std::memory_order_relaxed);
}
} // namespace webrtc
} // namespace unity
