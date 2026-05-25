using System.Runtime.InteropServices;
using System.Numerics;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using VorticeD3D11 = Vortice.Direct3D11.D3D11;

namespace DesktopPet.Rendering.D3D11;

public sealed class D3D11RenderHost : IRenderHost
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0
    ];

    private readonly Lock _sync = new();
    private readonly nint _hwnd;
    private readonly D3D11RenderLoop _renderLoop;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11VertexShader? _meshVertexShader;
    private ID3D11PixelShader? _meshPixelShader;
    private ID3D11InputLayout? _meshInputLayout;
    private ID3D11Buffer? _meshConstantsBuffer;
    private ID3D11BlendState? _alphaBlendState;
    private ID3D11BlendState? _colorWriteDisabledBlendState;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11RasterizerState? _cullingRasterizerState;
    private ID3D11DepthStencilState? _depthEnabledState;
    private ID3D11SamplerState? _textureSampler;
    private ID3D11SamplerState? _meshTextureSampler;
    private ID3D11Texture2D? _depthStencilTexture;
    private ID3D11DepthStencilView? _depthStencilView;
    private ID3D11DepthStencilState? _noDepthState;
    private ID3D11DepthStencilState? _stencilWriteState;
    private ID3D11DepthStencilState? _stencilEqualState;
    private ID3D11DepthStencilState? _stencilNotEqualState;
    private readonly Dictionary<string, ID3D11ShaderResourceView> _textureViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ID3D11ShaderResourceView> _meshTextureViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ID3D11Buffer Vertex, ID3D11Buffer Index, int VCount, int ICount)> _meshBufferCache = new(StringComparer.OrdinalIgnoreCase);

    // Live2D buffer cache: keyed by drawable index
    private readonly Dictionary<int, (ID3D11Buffer Buffer, int Capacity)> _live2DVertexBuffers = new();
    private readonly Dictionary<int, (ID3D11Buffer Buffer, int Count)> _live2DIndexBuffers = new();

    // Pre-sorted render items rebuilt on submission, not every frame
    private D3D11RenderItem[] _sortedRenderItems = [];
    private Dictionary<int, D3D11RenderItem> _renderItemsByDrawableIndex = new();
    private bool _renderItemsDirty;

    // Deferred resize: applied at frame boundary so it never races with Present
    private int _pendingResizeWidth;
    private int _pendingResizeHeight;
    private bool _resizePending;

    private int _width;
    private int _height;
    private bool _disposed;
    private long _frameCount;
    private DateTimeOffset _fpsStartedAt = DateTimeOffset.UtcNow;
    private string? _lastError;
    private D3D11RenderItem[] _renderItems = [];
    private D3D11MeshItem[] _meshItems = [];
    // Tracks what the mesh constant buffer currently contains; null = unknown (buffer may be dirty)
    private D3D11MeshConstants? _meshConstantsInBuffer;
    private bool _enableLive2DClipping;
    private bool _enableLive2DTextures;
    private bool _enableLive2DDrawing = true;
    private int _maxLive2DDrawItems = int.MaxValue;
    private float _modelScale = 1.0f;

    // Subtitle
    private readonly SubtitleRenderer _subtitleRenderer = new();
    private string? _subtitleText;
    private string? _subtitleLastRendered;
    private ID3D11Texture2D? _subtitleTexture;
    private ID3D11ShaderResourceView? _subtitleSrv;
    private ID3D11Buffer? _subtitleVertexBuffer;
    private ID3D11VertexShader? _subtitleVertexShader;
    private ID3D11PixelShader? _subtitlePixelShader;
    private ID3D11InputLayout? _subtitleInputLayout;

    public D3D11RenderHost(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.Handle == 0)
        {
            throw new ArgumentException("Render surface handle cannot be zero.", nameof(surface));
        }

        _hwnd = surface.Handle;
        _width = Math.Max(1, surface.Width);
        _height = Math.Max(1, surface.Height);
        _renderLoop = new D3D11RenderLoop(RenderFrame);
    }

    public RenderFrameStats FrameStats
    {
        get
        {
            var elapsed = Math.Max(0.001, (DateTimeOffset.UtcNow - _fpsStartedAt).TotalSeconds);
            return new RenderFrameStats(
                Interlocked.Read(ref _frameCount),
                Interlocked.Read(ref _frameCount) / elapsed,
                _lastError);
        }
    }

    public bool EnableLive2DClipping
    {
        get
        {
            lock (_sync)
            {
                return _enableLive2DClipping;
            }
        }
        set
        {
            lock (_sync)
            {
                _enableLive2DClipping = value;
            }
        }
    }

    public bool EnableLive2DTextures
    {
        get
        {
            lock (_sync)
            {
                return _enableLive2DTextures;
            }
        }
        set
        {
            lock (_sync)
            {
                _enableLive2DTextures = value;
            }
        }
    }

    public int MaxLive2DDrawItems
    {
        get
        {
            lock (_sync)
            {
                return _maxLive2DDrawItems;
            }
        }
        set
        {
            lock (_sync)
            {
                _maxLive2DDrawItems = Math.Max(0, value);
                _renderItemsDirty = true;
            }
        }
    }

    public bool EnableLive2DDrawing
    {
        get
        {
            lock (_sync)
            {
                return _enableLive2DDrawing;
            }
        }
        set
        {
            lock (_sync)
            {
                _enableLive2DDrawing = value;
            }
        }
    }

    public float ModelScale
    {
        get { lock (_sync) { return _modelScale; } }
        set { lock (_sync) { _modelScale = Math.Clamp(value, 0.1f, 5.0f); } }
    }

    public void SetSubtitle(string? text)
    {
        lock (_sync) { _subtitleText = text; }
    }

    public void Start()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            EnsureDeviceResources();
        }

        _renderLoop.Start();
    }

    public void Stop()
    {
        _renderLoop.Stop();
    }

    public void Resize(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
        {
            return;
        }

        // Store pending resize; the render thread applies it at frame boundary
        // (after Present returns) to avoid racing with in-flight Present calls.
        lock (_sync)
        {
            _pendingResizeWidth = width;
            _pendingResizeHeight = height;
            _resizePending = true;
        }
    }

    public void SubmitRenderItems(IReadOnlyList<D3D11RenderItem> renderItems)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(renderItems);

        lock (_sync)
        {
            _renderItems = renderItems.ToArray();
            _renderItemsDirty = true;
        }
    }

    public void SubmitMeshItems(IReadOnlyList<D3D11MeshItem> meshItems)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(meshItems);

        lock (_sync)
        {
            _meshItems = meshItems.ToArray();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderLoop.Dispose();

        lock (_sync)
        {
            ClearDeviceContextState();
            ReleaseRenderTarget();
            ReleaseDepthStencil();
            ReleasePipelineResources();
            ReleaseTextureResources();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _swapChain = null;
            _context = null;
            _device = null;
        }
    }

    private void ClearDeviceContextState()
    {
        if (_context is null)
        {
            return;
        }

        _context.ClearState();
        _context.Flush();
    }

    private void RenderFrame()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            IDXGISwapChain? chainToPresent = null;
            lock (_sync)
            {
                // Apply any pending resize before rendering the next frame.
                // This runs inside the lock but outside Present, which is safe.
                ProcessPendingResize();

                if (_renderTargetView is null || _context is null || _swapChain is null)
                {
                    return;
                }

                // Clear to fully transparent black so DWM per-pixel alpha compositing
                // shows the desktop through any un-drawn pixels.
                _context.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 0.0f));
                if (_depthStencilView is not null)
                {
                    _context.ClearDepthStencilView(_depthStencilView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);
                }

                DrawMeshItems();
                DrawRenderItems();
                DrawSubtitle();

                // Capture swap chain reference before releasing the lock.
                chainToPresent = _swapChain;
            }

            // Present OUTSIDE the main lock so Live2D / mesh submission threads
            // are not blocked during the vsync wait (~16 ms at 60 Hz).
            if (chainToPresent is not null && !_disposed)
            {
                var present = chainToPresent.Present(1);
                if (present.Failure)
                {
                    _lastError = $"Present failed: 0x{present.Code:X8}";
                    return;
                }

                Interlocked.Increment(ref _frameCount);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Thread.Sleep(100);
        }
    }

    // Called inside lock(_sync) at the start of each frame, never while Present is active.
    private void ProcessPendingResize()
    {
        if (!_resizePending || _swapChain is null)
        {
            return;
        }

        _resizePending = false;
        _width = _pendingResizeWidth;
        _height = _pendingResizeHeight;

        ReleaseRenderTarget();
        var result = _swapChain.ResizeBuffers(0, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        if (result.Failure)
        {
            _lastError = $"ResizeBuffers failed: 0x{result.Code:X8}";
            return;
        }

        CreateRenderTarget();
        CreateDepthStencil();
    }

    private void EnsureDeviceResources()
    {
        if (_device is not null)
        {
            return;
        }

        var swapChainDescription = new SwapChainDescription
        {
            BufferDescription = new ModeDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm
            },
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            OutputWindow = _hwnd,
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };

        var result = VorticeD3D11.D3D11CreateDeviceAndSwapChain(
            (IDXGIAdapter?)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels,
            swapChainDescription,
            out _swapChain,
            out _device,
            out _,
            out _context);

        if (result.Failure || _swapChain is null || _device is null || _context is null)
        {
            _lastError = $"D3D11CreateDeviceAndSwapChain failed: 0x{result.Code:X8}";
            throw new InvalidOperationException(_lastError);
        }

        CreateRenderTarget();
        CreateDepthStencil();
        CreatePipelineResources();
    }

    private void CreateRenderTarget()
    {
        if (_device is null || _swapChain is null)
        {
            return;
        }

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);
    }

    private void ReleaseRenderTarget()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
    }

    private void CreateDepthStencil()
    {
        if (_device is null)
        {
            return;
        }

        ReleaseDepthStencil();

        var textureDescription = new Texture2DDescription(
            Format.D24_UNorm_S8_UInt,
            (uint)_width,
            (uint)_height,
            1,
            1,
            BindFlags.DepthStencil,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);

        _depthStencilTexture = _device.CreateTexture2D(textureDescription);
        _depthStencilView = _device.CreateDepthStencilView(_depthStencilTexture);
    }

    private void ReleaseDepthStencil()
    {
        _depthStencilView?.Dispose();
        _depthStencilTexture?.Dispose();
        _depthStencilView = null;
        _depthStencilTexture = null;
    }

    private void CreatePipelineResources()
    {
        if (_device is null)
        {
            return;
        }

        const string shaderSource = """
            Texture2D Live2DTexture : register(t0);
            SamplerState Live2DSampler : register(s0);

            struct VertexIn
            {
                float2 Position : POSITION;
                float2 TexCoord : TEXCOORD0;
                float Opacity : OPACITY;
            };

            struct PixelIn
            {
                float4 Position : SV_POSITION;
                float2 TexCoord : TEXCOORD0;
                float Opacity : OPACITY;
            };

            PixelIn VSMain(VertexIn input)
            {
                PixelIn output;
                output.Position = float4(input.Position.x, input.Position.y, 0.0f, 1.0f);
                output.TexCoord = input.TexCoord;
                output.Opacity = input.Opacity;
                return output;
            }

            float4 PSMain(PixelIn input) : SV_TARGET
            {
                float4 color = Live2DTexture.Sample(Live2DSampler, input.TexCoord);
                if (color.a < 0.01f)
                {
                    discard;
                }

                color.a *= input.Opacity;
                return color;
            }
            """;

        using var vertexShaderBlob = D3D11ShaderCompiler.Compile(shaderSource, "VSMain", "vs_4_0");
        using var pixelShaderBlob = D3D11ShaderCompiler.Compile(shaderSource, "PSMain", "ps_4_0");

        _vertexShader = _device.CreateVertexShader(vertexShaderBlob);
        _pixelShader = _device.CreatePixelShader(pixelShaderBlob);
        _inputLayout = _device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                new InputElementDescription("OPACITY", 0, Format.R32_Float, 16, 0)
            ],
            vertexShaderBlob);

        const string meshShaderSource = """
            cbuffer MeshConstants : register(b0)
            {
                float4x4 WorldViewProjection;
                float4 BaseColorFactor;
                float HasTexture;
                float3 _Pad;
            };

            Texture2D MeshTexture : register(t1);
            SamplerState MeshSampler : register(s1);

            struct VertexIn
            {
                float3 Position : POSITION;
                float4 Color : COLOR0;
                float2 TexCoord : TEXCOORD0;
            };

            struct PixelIn
            {
                float4 Position : SV_POSITION;
                float4 Color : COLOR0;
                float2 TexCoord : TEXCOORD0;
            };

            PixelIn VSMain(VertexIn input)
            {
                PixelIn output;
                output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
                output.Color = input.Color;
                output.TexCoord = input.TexCoord;
                return output;
            }

            float4 PSMain(PixelIn input) : SV_TARGET
            {
                float4 base = HasTexture > 0.5f
                    ? MeshTexture.Sample(MeshSampler, input.TexCoord)
                    : input.Color;
                return base * BaseColorFactor;
            }
            """;

        using var meshVertexShaderBlob = D3D11ShaderCompiler.Compile(meshShaderSource, "VSMain", "vs_4_0");
        using var meshPixelShaderBlob = D3D11ShaderCompiler.Compile(meshShaderSource, "PSMain", "ps_4_0");
        _meshVertexShader = _device.CreateVertexShader(meshVertexShaderBlob);
        _meshPixelShader = _device.CreatePixelShader(meshPixelShaderBlob);
        _meshInputLayout = _device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 28, 0)
            ],
            meshVertexShaderBlob);
        _meshConstantsBuffer = _device.CreateBuffer(
            new BufferDescription(
                (uint)Marshal.SizeOf<D3D11MeshConstants>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0));
        _alphaBlendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        var noColorWriteBlend = BlendDescription.Opaque;
        noColorWriteBlend.RenderTarget[0].RenderTargetWriteMask = ColorWriteEnable.None;
        _colorWriteDisabledBlendState = _device.CreateBlendState(noColorWriteBlend);
        _rasterizerState = _device.CreateRasterizerState(CreateLive2DRasterizerDescription(CullMode.None));
        _cullingRasterizerState = _device.CreateRasterizerState(CreateLive2DRasterizerDescription(CullMode.Back));
        _depthEnabledState = _device.CreateDepthStencilState(new DepthStencilDescription(
            true,
            true,
            ComparisonFunction.LessEqual,
            false,
            0xFF,
            0xFF,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.Always,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.Always));
        _noDepthState = _device.CreateDepthStencilState(new DepthStencilDescription(
            false,
            false,
            ComparisonFunction.Always,
            false,
            0xFF,
            0xFF,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.Always,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.Always));
        _stencilWriteState = _device.CreateDepthStencilState(new DepthStencilDescription(
            false,
            true,
            ComparisonFunction.Always,
            true,
            0xFF,
            0xFF,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Replace,
            ComparisonFunction.Always,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Replace,
            ComparisonFunction.Always));
        _stencilEqualState = _device.CreateDepthStencilState(new DepthStencilDescription(
            false,
            true,
            ComparisonFunction.Always,
            true,
            0xFF,
            0,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.Equal,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.Equal));
        _stencilNotEqualState = _device.CreateDepthStencilState(new DepthStencilDescription(
            false,
            true,
            ComparisonFunction.Always,
            true,
            0xFF,
            0,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.NotEqual,
            StencilOperation.Keep,
            StencilOperation.Keep,
            StencilOperation.Keep,
            ComparisonFunction.NotEqual));
        _textureSampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Clamp,
            0.0f,
            1,
            ComparisonFunction.Always,
            0.0f,
            float.MaxValue));
        _meshTextureSampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Wrap,
            0.0f,
            1,
            ComparisonFunction.Always,
            0.0f,
            float.MaxValue));

        // ── 字幕 shader ───────────────────────────────────────────────────────
        const string subtitleShaderSource = """
            Texture2D SubtitleTex : register(t0);
            SamplerState SubtitleSampler : register(s0);

            struct VertexIn  { float2 Position : POSITION; float2 UV : TEXCOORD0; };
            struct PixelIn   { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };

            PixelIn VSMain(VertexIn v)
            {
                PixelIn o;
                o.Position = float4(v.Position, 0.0f, 1.0f);
                o.UV = v.UV;
                return o;
            }

            float4 PSMain(PixelIn p) : SV_TARGET
            {
                return SubtitleTex.Sample(SubtitleSampler, p.UV);
            }
            """;

        using var subVsBlob = D3D11ShaderCompiler.Compile(subtitleShaderSource, "VSMain", "vs_4_0");
        using var subPsBlob = D3D11ShaderCompiler.Compile(subtitleShaderSource, "PSMain", "ps_4_0");
        _subtitleVertexShader = _device.CreateVertexShader(subVsBlob);
        _subtitlePixelShader  = _device.CreatePixelShader(subPsBlob);
        _subtitleInputLayout  = _device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            ],
            subVsBlob);

        // Static quad vertex buffer — NDC coords filled each frame in DrawSubtitle
        _subtitleVertexBuffer = _device.CreateBuffer(new BufferDescription(
            4 * (uint)(4 * sizeof(float)),   // 4 vertices × (x,y,u,v)
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private static RasterizerDescription CreateLive2DRasterizerDescription(CullMode cullMode)
    {
        return new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = cullMode,
            FrontCounterClockwise = true,
            DepthClipEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
            DepthBias = 0,
            DepthBiasClamp = 0,
            SlopeScaledDepthBias = 0
        };
    }

    private void ReleasePipelineResources()
    {
        _rasterizerState?.Dispose();
        _cullingRasterizerState?.Dispose();
        _depthEnabledState?.Dispose();
        _meshConstantsBuffer?.Dispose();
        _meshInputLayout?.Dispose();
        _meshPixelShader?.Dispose();
        _meshVertexShader?.Dispose();
        _stencilNotEqualState?.Dispose();
        _stencilEqualState?.Dispose();
        _stencilWriteState?.Dispose();
        _noDepthState?.Dispose();
        _textureSampler?.Dispose();
        _meshTextureSampler?.Dispose();
        _colorWriteDisabledBlendState?.Dispose();
        _alphaBlendState?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _subtitleInputLayout?.Dispose();
        _subtitlePixelShader?.Dispose();
        _subtitleVertexShader?.Dispose();
        _subtitleVertexBuffer?.Dispose();
        _rasterizerState = null;
        _cullingRasterizerState = null;
        _depthEnabledState = null;
        _meshConstantsBuffer = null;
        _meshInputLayout = null;
        _meshPixelShader = null;
        _meshVertexShader = null;
        _stencilNotEqualState = null;
        _stencilEqualState = null;
        _stencilWriteState = null;
        _noDepthState = null;
        _textureSampler = null;
        _meshTextureSampler = null;
        _colorWriteDisabledBlendState = null;
        _alphaBlendState = null;
        _inputLayout = null;
        _pixelShader = null;
        _vertexShader = null;
        _subtitleInputLayout = null;
        _subtitlePixelShader = null;
        _subtitleVertexShader = null;
        _subtitleVertexBuffer = null;
    }

    private unsafe void DrawSubtitle()
    {
        if (_device is null
            || _context is null
            || _renderTargetView is null
            || _subtitleVertexShader is null
            || _subtitlePixelShader is null
            || _subtitleInputLayout is null
            || _subtitleVertexBuffer is null
            || _alphaBlendState is null
            || _noDepthState is null
            || _textureSampler is null)
        {
            return;
        }

        var text = _subtitleText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // ── 纹理重建：文字改变 或 窗口宽度改变时重建 ─────────────────────────
        // 字体固定大小，高度由实际行数决定，所以只检测宽度变化。
        var expectedTexW = (int)(_width * 0.90f);
        var needRebuild = !string.Equals(text, _subtitleLastRendered, StringComparison.Ordinal)
                          || _subtitleRenderer.TexWidth != expectedTexW;

        if (needRebuild)
        {
            _subtitleSrv?.Dispose();
            _subtitleTexture?.Dispose();
            _subtitleSrv = null;
            _subtitleTexture = null;

            var pixels = _subtitleRenderer.Render(text, _width, _height);
            if (pixels is null)
            {
                _subtitleLastRendered = text;
                return;
            }

            var texW = _subtitleRenderer.TexWidth;
            var texH = _subtitleRenderer.TexHeight;

            fixed (byte* p = pixels)
            {
                var desc = new Texture2DDescription(
                    Format.R8G8B8A8_UNorm,
                    (uint)texW, (uint)texH,
                    1, 1, BindFlags.ShaderResource,
                    ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
                var init = new SubresourceData(p, (uint)(texW * 4), 0);
                _subtitleTexture = _device.CreateTexture2D(desc, init);
                _subtitleSrv     = _device.CreateShaderResourceView(_subtitleTexture);
            }
            _subtitleLastRendered = text;
        }

        if (_subtitleSrv is null) return;

        // ── NDC quad：宽度固定 90%，高度由纹理像素高度换算 ───────────────────
        // 纹理高度 / 窗口高度 = quad NDC 高度 / 2（NDC 范围 -1..1）
        const float quadW     = 0.90f;
        const float bottomGap = 0.04f;   // 距底边留 4% 空白

        var ndcH = 2.0f * _subtitleRenderer.TexHeight / (float)Math.Max(1, _height);

        var x0 = -quadW;
        var x1 =  quadW;
        var y0 = -1.0f + bottomGap * 2f;
        var y1 =  y0 + ndcH;

        Span<float> verts = stackalloc float[]
        {
            x0, y1, 0f, 0f,   // 左上
            x1, y1, 1f, 0f,   // 右上
            x0, y0, 0f, 1f,   // 左下
            x1, y0, 1f, 1f,   // 右下
        };

        var mapped = _context.Map(_subtitleVertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        MemoryMarshal.AsBytes(verts).CopyTo(new Span<byte>((void*)mapped.DataPointer, verts.Length * sizeof(float)));
        _context.Unmap(_subtitleVertexBuffer, 0);

        // ── 绘制 ─────────────────────────────────────────────────────────────
        _context.OMSetRenderTargets(_renderTargetView);
        _context.OMSetBlendState(_alphaBlendState);
        _context.OMSetDepthStencilState(_noDepthState, 0);
        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.IASetInputLayout(_subtitleInputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetVertexBuffer(0, _subtitleVertexBuffer, 4 * sizeof(float), 0);
        _context.VSSetShader(_subtitleVertexShader);
        _context.PSSetShader(_subtitlePixelShader);
        _context.PSSetShaderResource(0, _subtitleSrv);
        _context.PSSetSampler(0, _textureSampler);
        _context.Draw(4, 0);
        _context.PSSetShaderResource(0, null!);
    }

    private void DrawRenderItems()
    {
        if (_device is null
            || _context is null
            || _renderTargetView is null
            || _vertexShader is null
            || _pixelShader is null
            || _inputLayout is null
            || _alphaBlendState is null
            || _colorWriteDisabledBlendState is null
            || _rasterizerState is null
            || _cullingRasterizerState is null
            || _textureSampler is null
            || _depthStencilView is null
            || _noDepthState is null
            || _stencilWriteState is null
            || _stencilEqualState is null
            || _stencilNotEqualState is null
            || !_enableLive2DDrawing
            || _renderItems.Length == 0)
        {
            return;
        }

        _context.OMSetRenderTargets(_renderTargetView, _depthStencilView);
        _context.OMSetBlendState(_alphaBlendState);
        _context.OMSetDepthStencilState(_noDepthState, 0);
        _context.RSSetState(_rasterizerState);
        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetSampler(0, _textureSampler);

        // Rebuild the sorted list and drawable-index lookup only when submission changed it,
        // not every frame (saves allocations at 60 fps).
        if (_renderItemsDirty)
        {
            _sortedRenderItems = _renderItems
                .OrderBy(item => item.RenderOrder)
                .ThenBy(item => item.DrawableIndex)
                .Take(_maxLive2DDrawItems)
                .ToArray();
            _renderItemsByDrawableIndex = _sortedRenderItems.ToDictionary(item => item.DrawableIndex);
            _renderItemsDirty = false;
        }

        var transform = CreateLive2DTransform(_sortedRenderItems);
        foreach (var item in _sortedRenderItems)
        {
            if (!CanDraw(item))
            {
                continue;
            }

            if (_enableLive2DClipping)
            {
                DrawMaskedItem(_renderItemsByDrawableIndex, item, transform);
            }
            else
            {
                DrawItem(item, transform);
            }
        }

        _context.OMSetDepthStencilState(_noDepthState, 0);
    }

    private void DrawMeshItems()
    {
        if (_device is null
            || _context is null
            || _renderTargetView is null
            || _depthStencilView is null
            || _meshVertexShader is null
            || _meshPixelShader is null
            || _meshInputLayout is null
            || _meshConstantsBuffer is null
            || _alphaBlendState is null
            || _rasterizerState is null
            || _depthEnabledState is null
            || _meshItems.Length == 0)
        {
            return;
        }

        _context.OMSetRenderTargets(_renderTargetView, _depthStencilView);
        _context.OMSetBlendState(_alphaBlendState);
        _context.OMSetDepthStencilState(_depthEnabledState, 0);
        _context.RSSetState(_rasterizerState);
        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.IASetInputLayout(_meshInputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_meshVertexShader);
        _context.VSSetConstantBuffer(0, _meshConstantsBuffer);
        _context.PSSetShader(_meshPixelShader);

        foreach (var meshItem in _meshItems)
        {
            DrawMeshItem(meshItem);
        }

        // Restore explicit no-depth state (do not pass null; null re-enables depth test by default).
        _context.OMSetDepthStencilState(_noDepthState, 0);
    }

    private void DrawMeshItem(D3D11MeshItem item)
    {
        if (_device is null || _context is null || _meshConstantsBuffer is null || item.Vertices.Count == 0 || item.Indices.Count == 0)
        {
            return;
        }

        var (vertexBuffer, indexBuffer) = GetOrCreateMeshBuffers(item);

        var world = item.WorldTransform;
        var cameraDistance = 2.8f / _modelScale;
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0.0f, 0.15f, cameraDistance),
            new Vector3(0.0f, 0.0f, 0.0f),
            Vector3.UnitY);
        var aspectRatio = Math.Max(0.1f, _width / (float)Math.Max(1, _height));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4.0f,
            aspectRatio,
            0.05f,
            32.0f);

        var textureView = item.Texture is not null ? GetOrCreateMeshTextureView(item.Texture) : null;
        var baseColorFactor = item.BaseColorFactor == default ? Vector4.One : item.BaseColorFactor;
        var constants = new D3D11MeshConstants(
            Matrix4x4.Transpose(world * view * projection),
            baseColorFactor,
            textureView is not null ? 1.0f : 0.0f);

        if (!_meshConstantsInBuffer.HasValue || _meshConstantsInBuffer.Value != constants)
        {
            _context.UpdateSubresource(constants, _meshConstantsBuffer);
            _meshConstantsInBuffer = constants;
        }
        _context.IASetVertexBuffer(0, vertexBuffer, (uint)Marshal.SizeOf<D3D11MeshVertex>(), 0);
        _context.IASetIndexBuffer(indexBuffer, Format.R16_UInt, 0);
        if (textureView is not null)
        {
            _context.PSSetShaderResource(1, textureView);
        }

        _context.PSSetSampler(1, _meshTextureSampler);
        _context.DrawIndexed((uint)item.Indices.Count, 0, 0);
    }

    private (ID3D11Buffer Vertex, ID3D11Buffer Index) GetOrCreateMeshBuffers(D3D11MeshItem item)
    {
        if (_meshBufferCache.TryGetValue(item.Id, out var cached)
            && cached.VCount == item.Vertices.Count
            && cached.ICount == item.Indices.Count)
        {
            return (cached.Vertex, cached.Index);
        }

        if (_meshBufferCache.Remove(item.Id, out var old))
        {
            old.Vertex.Dispose();
            old.Index.Dispose();
        }

        var vb = _device!.CreateBuffer(
            item.Vertices.ToArray(), BindFlags.VertexBuffer, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0, 0);
        var ib = _device.CreateBuffer(
            item.Indices.ToArray(), BindFlags.IndexBuffer, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0, 0);
        _meshBufferCache[item.Id] = (vb, ib, item.Vertices.Count, item.Indices.Count);
        return (vb, ib);
    }

    private unsafe ID3D11ShaderResourceView? GetOrCreateMeshTextureView(D3D11MeshTexture texture)
    {
        if (_device is null)
        {
            return null;
        }

        if (_meshTextureViews.TryGetValue(texture.CacheKey, out var existing))
        {
            return existing;
        }

        fixed (byte* pixelPointer = texture.RgbaPixels)
        {
            var textureDescription = new Texture2DDescription(
                Format.R8G8B8A8_UNorm,
                (uint)texture.Width,
                (uint)texture.Height,
                1,
                1,
                BindFlags.ShaderResource);
            var initialData = new SubresourceData(
                pixelPointer,
                checked((uint)(texture.Width * 4)),
                checked((uint)(texture.RgbaPixels.Length)));
            using var tex = _device.CreateTexture2D(textureDescription, initialData);
            var srv = _device.CreateShaderResourceView(tex);
            _meshTextureViews.Add(texture.CacheKey, srv);
            return srv;
        }
    }

    private void DrawMaskedItem(
        IReadOnlyDictionary<int, D3D11RenderItem> renderItemsByDrawableIndex,
        D3D11RenderItem item,
        Live2DTransform transform)
    {
        if (_context is null
            || _depthStencilView is null
            || _noDepthState is null
            || _stencilWriteState is null
            || _stencilEqualState is null
            || _stencilNotEqualState is null
            || _alphaBlendState is null
            || _colorWriteDisabledBlendState is null)
        {
            return;
        }

        if (item.MaskIndices.Count > 0)
        {
            _context.ClearDepthStencilView(_depthStencilView, DepthStencilClearFlags.Stencil, 1.0f, 0);
            _context.OMSetBlendState(_colorWriteDisabledBlendState);
            _context.OMSetDepthStencilState(_stencilWriteState, 1);

            foreach (var maskIndex in item.MaskIndices)
            {
                // Draw mask shapes to stencil regardless of visibility/opacity.
                // In Live2D, clip-mask drawables may be flagged invisible while still
                // providing valid geometry for the stencil pass.
                if (renderItemsByDrawableIndex.TryGetValue(maskIndex, out var maskItem) && CanDrawAsMask(maskItem))
                {
                    DrawItem(maskItem, transform);
                }
            }

            _context.OMSetBlendState(_alphaBlendState);
            _context.OMSetDepthStencilState(item.IsInvertedMask ? _stencilNotEqualState : _stencilEqualState, 1);
            DrawItem(item, transform);
            _context.OMSetDepthStencilState(_noDepthState, 0);
            return;
        }

        _context.OMSetBlendState(_alphaBlendState);
        _context.OMSetDepthStencilState(_noDepthState, 0);
        DrawItem(item, transform);
    }

    private void DrawItem(D3D11RenderItem item, Live2DTransform transform)
    {
        if (_device is null || _context is null)
        {
            return;
        }

        var textureView = _enableLive2DTextures ? GetTextureView(item.TexturePath) : null;
        if (_enableLive2DTextures && textureView is null)
        {
            return;
        }

        _context.RSSetState(_rasterizerState);

        var vertices = CreateVertices(item, transform);
        var vertexBuffer = GetOrUpdateLive2DVertexBuffer(item.DrawableIndex, vertices);
        var indexBuffer = GetOrCreateLive2DIndexBuffer(item.DrawableIndex, item.Indices);

        _context.IASetVertexBuffer(0, vertexBuffer, (uint)Marshal.SizeOf<D3D11Live2DVertex>(), 0);
        _context.IASetIndexBuffer(indexBuffer, Format.R16_UInt, 0);
        if (textureView is not null)
        {
            _context.PSSetShaderResource(0, textureView);
        }

        _context.DrawIndexed((uint)item.Indices.Count, 0, 0);
        _context.PSSetShaderResource(0, null!);
    }

    // Gets a cached DYNAMIC vertex buffer and updates it via Map(WriteDiscard).
    // Eliminates per-frame D3D11 buffer allocation for each Live2D drawable.
    private unsafe ID3D11Buffer GetOrUpdateLive2DVertexBuffer(int drawableIndex, D3D11Live2DVertex[] vertices)
    {
        var vertexSize = Marshal.SizeOf<D3D11Live2DVertex>();
        var sizeInBytes = (uint)(vertices.Length * vertexSize);

        ID3D11Buffer buffer;
        if (_live2DVertexBuffers.TryGetValue(drawableIndex, out var cached) && cached.Capacity >= vertices.Length)
        {
            buffer = cached.Buffer;
        }
        else
        {
            if (_live2DVertexBuffers.Remove(drawableIndex, out var old))
            {
                old.Buffer.Dispose();
            }

            var desc = new BufferDescription(sizeInBytes, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            buffer = _device!.CreateBuffer(desc);
            _live2DVertexBuffers[drawableIndex] = (buffer, vertices.Length);
        }

        var mapped = _context!.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        MemoryMarshal.AsBytes(vertices.AsSpan()).CopyTo(
            new Span<byte>((void*)mapped.DataPointer, (int)sizeInBytes));
        _context.Unmap(buffer, 0);

        return buffer;
    }

    // Gets or creates a cached IMMUTABLE index buffer for a Live2D drawable.
    // Indices never change for a given drawable, so one allocation per drawable suffices.
    private ID3D11Buffer GetOrCreateLive2DIndexBuffer(int drawableIndex, IReadOnlyList<ushort> indices)
    {
        if (_live2DIndexBuffers.TryGetValue(drawableIndex, out var cached) && cached.Count == indices.Count)
        {
            return cached.Buffer;
        }

        if (_live2DIndexBuffers.Remove(drawableIndex, out var old))
        {
            old.Buffer.Dispose();
        }

        var ib = _device!.CreateBuffer(
            indices.ToArray(),
            BindFlags.IndexBuffer,
            ResourceUsage.Immutable,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0, 0);

        _live2DIndexBuffers[drawableIndex] = (ib, indices.Count);
        return ib;
    }

    private static bool CanDraw(D3D11RenderItem item)
    {
        return item.IsVisible && item.VertexPositions.Count >= 6 && item.Indices.Count >= 3 && item.Opacity > 0.001f;
    }

    // Less strict than CanDraw: masks only need valid geometry to write stencil.
    // The Live2D SDK renders clip-mask drawables unconditionally (they may be
    // invisible in normal rendering but still define clip boundaries).
    private static bool CanDrawAsMask(D3D11RenderItem item)
    {
        return item.VertexPositions.Count >= 6 && item.Indices.Count >= 3;
    }

    private ID3D11ShaderResourceView? GetTextureView(string? texturePath)
    {
        if (_device is null || string.IsNullOrWhiteSpace(texturePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(texturePath);
        if (_textureViews.TryGetValue(fullPath, out var existing))
        {
            return existing;
        }

        var image = PngRgbaDecoder.Decode(fullPath);
        unsafe
        {
            fixed (byte* pixelPointer = image.RgbaPixels)
            {
                var textureDescription = new Texture2DDescription(
                    Format.R8G8B8A8_UNorm,
                    (uint)image.Width,
                    (uint)image.Height,
                    1,
                    1,
                    BindFlags.ShaderResource,
                    ResourceUsage.Immutable,
                    CpuAccessFlags.None,
                    1,
                    0,
                    ResourceOptionFlags.None);
                var initialData = new SubresourceData(
                    pixelPointer,
                    checked((uint)(image.Width * 4)),
                    checked((uint)(image.RgbaPixels.Length)));
                using var texture = _device.CreateTexture2D(textureDescription, initialData);
                var textureView = _device.CreateShaderResourceView(texture);
                _textureViews.Add(fullPath, textureView);
                return textureView;
            }
        }
    }

    private void ReleaseTextureResources()
    {
        foreach (var textureView in _textureViews.Values)
        {
            textureView.Dispose();
        }

        _textureViews.Clear();

        foreach (var textureView in _meshTextureViews.Values)
        {
            textureView.Dispose();
        }

        _meshTextureViews.Clear();

        foreach (var entry in _meshBufferCache.Values)
        {
            entry.Vertex.Dispose();
            entry.Index.Dispose();
        }

        _meshBufferCache.Clear();

        foreach (var entry in _live2DVertexBuffers.Values)
        {
            entry.Buffer.Dispose();
        }

        _live2DVertexBuffers.Clear();

        foreach (var entry in _live2DIndexBuffers.Values)
        {
            entry.Buffer.Dispose();
        }

        _live2DIndexBuffers.Clear();

        // Subtitle
        _subtitleSrv?.Dispose();
        _subtitleTexture?.Dispose();
        _subtitleSrv = null;
        _subtitleTexture = null;
        _subtitleLastRendered = null;
        // SubtitleRenderer has no unmanaged resources; no Dispose needed.
    }

    private Live2DTransform CreateLive2DTransform(IReadOnlyList<D3D11RenderItem> items)
    {
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;

        foreach (var item in items)
        {
            for (var i = 0; i + 1 < item.VertexPositions.Count; i += 2)
            {
                var x = item.VertexPositions[i];
                var y = item.VertexPositions[i + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
        {
            return Live2DTransform.Identity;
        }

        var halfWidth = Math.Max(0.001f, (maxX - minX) * 0.5f);
        var halfHeight = Math.Max(0.001f, (maxY - minY) * 0.5f);
        var aspect = _width / (float)Math.Max(1, _height);

        // Base scale fits the model into the window at scale=1.
        const float margin = 0.88f;
        var baseScale = Math.Min(margin / halfWidth, margin / (halfHeight * aspect));
        var scale = baseScale * _modelScale;

        // ── 方案 A：以坐标原点 (0,0) 为缩放中心 ──────────────────────────────
        // Live2D 设计师以画布中心作为坐标系原点，用 (0,0) 避免偏心放大漂移。
        var centerX = 0.0f;
        var centerY = 0.0f;

        // ── 方案 B：边界 clamp，防止放大后模型超出窗口 ──────────────────────
        // 模型在 NDC 空间中的半宽/半高 = model-space-half * scale（Y 轴还要除 aspect）。
        // 如果超出 [-1, 1]，把中心往原点方向推回刚好不超边界。
        var ndcHalfW = halfWidth * scale;
        var ndcHalfH = halfHeight * scale / aspect;

        if (ndcHalfW > 1.0f)
        {
            // 模型宽度超出：将 centerX clamp 到 [-(ndcHalfW-1), +(ndcHalfW-1)] 的逆映射
            // 即 center 在 model-space 里最多偏移 (ndcHalfW - 1) / scale
            var maxOffsetX = (ndcHalfW - 1.0f) / scale;
            centerX = Math.Clamp(centerX, -maxOffsetX, maxOffsetX);
        }

        if (ndcHalfH > 1.0f)
        {
            var maxOffsetY = (ndcHalfH - 1.0f) / (scale / aspect);
            centerY = Math.Clamp(centerY, -maxOffsetY, maxOffsetY);
        }

        return new Live2DTransform(centerX, centerY, scale, scale * aspect);
    }

    private static D3D11Live2DVertex[] CreateVertices(D3D11RenderItem item, Live2DTransform transform)
    {
        var vertexCount = item.VertexPositions.Count / 2;
        var vertices = new D3D11Live2DVertex[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var positionIndex = i * 2;
            var u = item.VertexUvs.Count > positionIndex ? item.VertexUvs[positionIndex] : 0.0f;
            var v = item.VertexUvs.Count > positionIndex + 1 ? 1.0f - item.VertexUvs[positionIndex + 1] : 0.0f;
            vertices[i] = new D3D11Live2DVertex(
                (item.VertexPositions[positionIndex] - transform.CenterX) * transform.ScaleX,
                (item.VertexPositions[positionIndex + 1] - transform.CenterY) * transform.ScaleY,
                u,
                v,
                Math.Clamp(item.Opacity, 0.0f, 1.0f));
        }

        return vertices;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct Live2DTransform(float CenterX, float CenterY, float ScaleX, float ScaleY)
    {
        public static Live2DTransform Identity { get; } = new(0.0f, 0.0f, 1.0f, 1.0f);
    }
}
