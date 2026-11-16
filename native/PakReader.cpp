#include "Pak.h"
#include <cstring>
#include <limits>
#include <stdexcept>

// Read-only ABI over the same parser/decoder used by EIRebuild64Pack.
// No exceptions, STL objects or owned buffers cross the managed boundary.
namespace {
thread_local std::string lastError;
template<class Action> int Guard(Action action) noexcept {
    try { action(); lastError.clear(); return 0; }
    catch (const std::exception& e) { lastError = e.what(); return -1; }
    catch (...) { lastError = "Unknown PAK reader error"; return -1; }
}
ei::pak::Archive& Archive(void* handle) {
    if (!handle) throw std::runtime_error("Invalid PAK reader handle");
    return *static_cast<ei::pak::Archive*>(handle);
}
}
struct PakEntryInfo {
    const char* name;
    uint32_t nameLength;
    uint32_t method;
    uint64_t size;
};
static_assert(sizeof(PakEntryInfo) == 24, "The reader ABI is x64 only");
#define API extern "C" __declspec(dllexport)
API const char* ei_pak_error() noexcept { return lastError.c_str(); }
API int ei_pak_open(const wchar_t* path, void** result) noexcept {
    return Guard([&] {
        if (!path || !result) throw std::runtime_error("Missing PAK path/output");
        *result = nullptr;
        *result = new ei::pak::Archive(path);
    });
}
API void ei_pak_close(void* handle) noexcept { delete static_cast<ei::pak::Archive*>(handle); }
API int ei_pak_count(void* handle, uint32_t* count) noexcept {
    return Guard([&] {
        const auto size = Archive(handle).Entries().size();
        if (!count || size > UINT32_MAX) throw std::runtime_error("PAK entry count overflow");
        *count = static_cast<uint32_t>(size);
    });
}
API int ei_pak_entry(void* handle, uint32_t index, PakEntryInfo* result) noexcept {
    return Guard([&] {
        const auto& entry = Archive(handle).Entries().at(index);
        if (!result || entry.name.size() > UINT32_MAX) throw std::runtime_error("PAK name overflow");
        *result = {entry.name.data(), static_cast<uint32_t>(entry.name.size()), entry.method, entry.size};
    });
}
API int ei_pak_read(void* handle, uint32_t index, uint8_t* destination, uint32_t capacity) noexcept {
    return Guard([&] {
        auto& archive = Archive(handle);
        const auto& entry = archive.Entries().at(index);
        if (capacity > entry.size || (capacity && !destination)) throw std::runtime_error("Invalid PAK read buffer");
        // Even a prefix read verifies the entire decompressed resource checksum.
        const auto bytes = archive.Read(entry, true);
        if (capacity) std::memcpy(destination, bytes.data(), capacity);
    });
}
