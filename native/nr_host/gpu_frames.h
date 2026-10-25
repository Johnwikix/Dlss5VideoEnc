#pragma once
// CUDA imports these committed D3D12 buffers on the same adapter. The caller
// finishes its CUDA stream before Evaluate and consumes output only after the
// native fence completes. No CPU motion conversion or frame readback occurs.
namespace gpu_frames {
Staging buffers[kMaxSlots][3]; // color, half2 motion, output
HANDLE handles[kMaxSlots][3] = {};
UINT64 allocations[kMaxSlots][3] = {};
bool active = false;
int current = -1;

void Close() {
    active = false; current = -1;
    for (int i = 0; i < kMaxSlots; ++i) for (int kind = 0; kind < 3; ++kind) {
        ReleaseStaging(buffers[i][kind]);
        if (handles[i][kind]) CloseHandle(handles[i][kind]);
        handles[i][kind] = nullptr; allocations[i][kind] = 0;
    }
}

bool Create() {
    if (!g_ready || g_pending_count || g_config.tiled_subrects ||
        !g_config.persistent_buffers || !g_config.merged_submission || g_config.zero_guidance_fast_path)
        return false;
    Close();
    const auto heap = HeapProperties(D3D12_HEAP_TYPE_DEFAULT);
    for (int i = 0; i < g_slot_count; ++i) for (int kind = 0; kind < 3; ++kind) {
        auto &buffer = buffers[i][kind];
        const auto texture = (kind == 0 ? g_slots[i].color : kind == 1 ? g_slots[i].motion : g_slots[i].output)->GetDesc();
        g_device->GetCopyableFootprints(&texture, 0, 1, 0, &buffer.footprint, &buffer.rows, &buffer.row_size, &buffer.total_size);
        const auto description = BufferDescription(buffer.total_size);
        if (FAILED(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &description,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&buffer.resource))) ||
            FAILED(g_device->CreateSharedHandle(buffer.resource, nullptr, GENERIC_ALL, nullptr, &handles[i][kind]))) {
            Close(); return false;
        }
        allocations[i][kind] = g_device->GetResourceAllocationInfo(0, 1, &description).SizeInBytes;
    }
    return true;
}
}
