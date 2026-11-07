// Minimal DXGI_FORMAT stub.
//
// imgui_impl_dx12.h includes <d3d12.h>, whose MinGW variant pulls in
// <immintrin.h>. The bundled libclang does not know the AVX512 builtins that
// header references, so generation aborts. Only a handful of DX12 types are
// actually needed, and the generated code remaps them onto Silk.NET anyway,
// so a stub is sufficient.

#pragma once

typedef enum DXGI_FORMAT
{
    DXGI_FORMAT_UNKNOWN = 0,
    DXGI_FORMAT_R32G32B32A32_FLOAT = 2,
    DXGI_FORMAT_R8G8B8A8_UNORM = 28,
    DXGI_FORMAT_R8G8B8A8_UNORM_SRGB = 29,
    DXGI_FORMAT_D32_FLOAT = 40,
} DXGI_FORMAT;
