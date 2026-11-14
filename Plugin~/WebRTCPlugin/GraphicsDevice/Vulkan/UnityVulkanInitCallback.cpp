#include "pch.h"

#include <algorithm>
#include <atomic>
#include <cstring>

#include "UnityVulkanInitCallback.h"
#if UNITY_ANDROID
#include <android/log.h>
#include <cstdlib>
#include <sys/system_properties.h>
#endif

namespace unity
{
namespace webrtc
{
    static std::atomic<bool> s_ahbVulkanFeaturesEnabled { false };
    bool AhbVulkanFeaturesEnabled() { return s_ahbVulkanFeaturesEnabled; }
#if UNITY_ANDROID
    static VkInstance s_createdInstance = VK_NULL_HANDLE;
#endif
    const std::vector<const char*> requestedInstanceExtensions = {
        VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_EXTENSION_NAME,
        VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME,
        VK_KHR_EXTERNAL_SEMAPHORE_CAPABILITIES_EXTENSION_NAME
    };

    static std::vector<const char*> requestedDeviceExtensions = { VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME,
                                                                  VK_KHR_EXTERNAL_SEMAPHORE_EXTENSION_NAME,
#ifdef UNITY_LINUX
                                                                  VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME,
                                                                  VK_KHR_EXTERNAL_SEMAPHORE_FD_EXTENSION_NAME
#elif UNITY_WIN
                                                                  VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME,
                                                                  VK_KHR_EXTERNAL_SEMAPHORE_WIN32_EXTENSION_NAME
#endif
    };

    std::vector<const char*>
    MergeExtensionNames(const char* const* enabled, uint32_t enabledCount, const std::vector<const char*>& requested)
    {
        std::vector<const char*> names;
        names.reserve(enabledCount + requested.size());
        for (uint32_t i = 0; i < enabledCount; ++i)
            names.push_back(enabled[i]);
        for (const char* name : requested)
        {
            auto same = [name](const char* other) { return std::strcmp(name, other) == 0; };
            if (std::none_of(names.begin(), names.end(), same))
                names.push_back(name);
        }
        return names;
    }

    static VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateDevice(
        VkPhysicalDevice physicalDevice,
        const VkDeviceCreateInfo* pCreateInfo,
        const VkAllocationCallbacks* pAllocator,
        VkDevice* pDevice)
    {
        // copy value
        VkDeviceCreateInfo newCreateInfo = *pCreateInfo;

        std::vector<const char*> newExtensions = MergeExtensionNames(
            pCreateInfo->ppEnabledExtensionNames, pCreateInfo->enabledExtensionCount, requestedDeviceExtensions);

#if UNITY_ANDROID
        // Prototype capability gate: preserve normal Vulkan startup when AHB is unavailable.
        const std::vector<const char*> ahbExtensions = {
            "VK_ANDROID_external_memory_android_hardware_buffer", "VK_KHR_dedicated_allocation",
            "VK_KHR_get_memory_requirements2", "VK_KHR_bind_memory2", "VK_EXT_queue_family_foreign",
            "VK_KHR_maintenance1", "VK_KHR_sampler_ycbcr_conversion"
        };
        char mode[PROP_VALUE_MAX] {};
        const bool requestedAhb = __system_property_get("debug.ahb.mode", mode) > 0 && std::atoi(mode) >= 2;
        uint32_t extensionCount = 0;
        bool supported = requestedAhb &&
            vkEnumerateDeviceExtensionProperties(physicalDevice, nullptr, &extensionCount, nullptr) == VK_SUCCESS;
        std::vector<VkExtensionProperties> available(extensionCount);
        if (supported)
            supported = vkEnumerateDeviceExtensionProperties(physicalDevice, nullptr, &extensionCount, available.data()) == VK_SUCCESS;
        for (const auto* name : ahbExtensions)
            supported = supported && std::any_of(available.begin(), available.end(),
                [name](const VkExtensionProperties& ext) { return std::strcmp(name, ext.extensionName) == 0; });

        VkPhysicalDeviceSamplerYcbcrConversionFeatures queried {
            VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SAMPLER_YCBCR_CONVERSION_FEATURES, nullptr, VK_FALSE };
        VkPhysicalDeviceFeatures2 features { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, &queried, {} };
        auto getFeatures = reinterpret_cast<PFN_vkGetPhysicalDeviceFeatures2>(
            vkGetInstanceProcAddr(s_createdInstance, "vkGetPhysicalDeviceFeatures2"));
        if (!getFeatures)
            getFeatures = reinterpret_cast<PFN_vkGetPhysicalDeviceFeatures2>(
                vkGetInstanceProcAddr(s_createdInstance, "vkGetPhysicalDeviceFeatures2KHR"));
        if (getFeatures)
            getFeatures(physicalDevice, &features);
        supported = supported && queried.samplerYcbcrConversion;

        // Do not add a duplicate feature structure or modify Unity's const pNext chain.
        bool alreadySpecified = false;
        bool alreadyEnabled = false;
        for (auto* entry = static_cast<const VkBaseInStructure*>(pCreateInfo->pNext); entry; entry = entry->pNext)
        {
            if (entry->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SAMPLER_YCBCR_CONVERSION_FEATURES)
            {
                alreadySpecified = true;
                alreadyEnabled = reinterpret_cast<const VkPhysicalDeviceSamplerYcbcrConversionFeatures*>(entry)->samplerYcbcrConversion;
            }
            if (entry->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES)
            {
                alreadySpecified = true;
                alreadyEnabled = reinterpret_cast<const VkPhysicalDeviceVulkan11Features*>(entry)->samplerYcbcrConversion;
            }
        }
        supported = supported && (!alreadySpecified || alreadyEnabled);
        VkPhysicalDeviceSamplerYcbcrConversionFeatures requested {
            VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SAMPLER_YCBCR_CONVERSION_FEATURES,
            const_cast<void*>(pCreateInfo->pNext), VK_TRUE };
        if (supported)
        {
            newExtensions = MergeExtensionNames(newExtensions.data(), static_cast<uint32_t>(newExtensions.size()), ahbExtensions);
            if (!alreadySpecified)
                newCreateInfo.pNext = &requested;
        }
        s_ahbVulkanFeaturesEnabled = supported;
        __android_log_print(ANDROID_LOG_INFO, "AhbH264", "Vulkan AHB capability=%d existingYCbCr=%d enabled=%d",
            supported, alreadySpecified, alreadyEnabled);
#endif

        RTC_LOG(LS_INFO) << "WebRTC plugin intercepts vkCreateDevice.";

        for (auto extension : newExtensions)
        {
            RTC_LOG(LS_INFO) << "[Vulkan init intercept] extensions: name=" << extension;
        }

        // replace extension name list
        newCreateInfo.ppEnabledExtensionNames = newExtensions.data();
        newCreateInfo.enabledExtensionCount = static_cast<uint32_t>(newExtensions.size());
        VkResult result = vkCreateDevice(physicalDevice, &newCreateInfo, pAllocator, pDevice);
#if UNITY_ANDROID
        if (result != VK_SUCCESS && s_ahbVulkanFeaturesEnabled)
        {
            // A driver may advertise the required capabilities but reject their
            // combination. Retry Unity's normal configuration without AHB.
            s_ahbVulkanFeaturesEnabled = false;
            RTC_LOG(LS_WARNING) << "AHB Vulkan device creation failed; retrying without AHB: " << result;
            newExtensions = MergeExtensionNames(pCreateInfo->ppEnabledExtensionNames,
                pCreateInfo->enabledExtensionCount, requestedDeviceExtensions);
            newCreateInfo = *pCreateInfo;
            newCreateInfo.ppEnabledExtensionNames = newExtensions.data();
            newCreateInfo.enabledExtensionCount = static_cast<uint32_t>(newExtensions.size());
            result = vkCreateDevice(physicalDevice, &newCreateInfo, pAllocator, pDevice);
        }
#endif
        if (result != VK_SUCCESS)
        {
            s_ahbVulkanFeaturesEnabled = false;
            RTC_LOG(LS_ERROR) << "vkCreateDevice failed. error:" << result;
            return result;
        }
        if (!LoadDeviceVulkanFunction(*pDevice))
            return VK_ERROR_INITIALIZATION_FAILED;
        return result;
    }

    static VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateInstance(
        const VkInstanceCreateInfo* pCreateInfo, const VkAllocationCallbacks* pAllocator, VkInstance* pInstance)
    {
        if (!LoadGlobalVulkanFunction())
            return VK_ERROR_INITIALIZATION_FAILED;

        // copy value
        VkInstanceCreateInfo newCreateInfo = *pCreateInfo;

        std::vector<const char*> newExtensions = MergeExtensionNames(
            pCreateInfo->ppEnabledExtensionNames, pCreateInfo->enabledExtensionCount, requestedInstanceExtensions);

        RTC_LOG(LS_INFO) << "WebRTC plugin intercepts vkCreateInstance.";

        for (auto extension : newExtensions)
        {
            RTC_LOG(LS_INFO) << "[Vulkan init intercept] extensions: name=" << extension;
        }

        // replace extension name list
        newCreateInfo.ppEnabledExtensionNames = newExtensions.data();
        newCreateInfo.enabledExtensionCount = static_cast<uint32_t>(newExtensions.size());

        VkResult result = vkCreateInstance(&newCreateInfo, pAllocator, pInstance);
        if (result != VK_SUCCESS)
        {
            RTC_LOG(LS_ERROR) << "vkCreateInstance failed. error:" << result;
            return result;
        }

        if (!LoadInstanceVulkanFunction(*pInstance))
            return VK_ERROR_INITIALIZATION_FAILED;
#if UNITY_ANDROID
        s_createdInstance = *pInstance;
#endif
        return result;
    }

    static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL
    Hook_vkGetInstanceProcAddr(VkInstance instance, const char* funcName)
    {
        if (!funcName)
            return nullptr;

        std::string strFuncName = funcName;

        if (strFuncName == "vkCreateInstance")
        {
            return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkCreateInstance);
        }
        if (strFuncName == "vkCreateDevice")
        {
            return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkCreateDevice);
        }
        return vkGetInstanceProcAddr(instance, funcName);
    }

    PFN_vkGetInstanceProcAddr
    InterceptVulkanInitialization(PFN_vkGetInstanceProcAddr getInstanceProcAddr, void* userdata)
    {
        vkGetInstanceProcAddr = getInstanceProcAddr;
        return Hook_vkGetInstanceProcAddr;
    }

}
}
