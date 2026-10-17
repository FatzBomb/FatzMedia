using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>
/// Graphics pipelines drawn after the clear and background blit: a full-target wash and the
/// nearest-sampled CDG overlay. Both blend premultiplied colour ONE / ONE_MINUS_SRC_ALPHA into
/// the BGRA8 target inside one render pass that loads and stores the target. SPIR-V is compiled
/// from <c>Shaders/*.vert|*.frag</c> (scripts/compile-vulkan-shaders.sh) and embedded.
/// </summary>
internal sealed unsafe class VulkanOverlayPipelines : IDisposable
{
    // One vec4 of push constants at offset 0, visible to both stages: the vertex stage reads it as
    // the CDG texture rectangle, the wash fragment stage as the wash colour (the wash quad ignores
    // texture coordinates). Keeping every read at offset 0 also sidesteps a Mesa lavapipe
    // (LLVM 20) issue where a fragment-stage vec4 push-constant load at offset 16 returned the
    // bytes at offset 0.
    private const uint PushConstantSize = 4 * sizeof(float);

    private readonly Vk _vk;
    private readonly Device _device;
    private RenderPass _renderPass;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _cdgDescriptorSet;
    private Sampler _sampler;
    private VkPipeline _washPipeline;
    private VkPipeline _cdgPipeline;

    private VulkanOverlayPipelines(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>Creates every pipeline object, releasing the partial set on failure.</summary>
    public static VulkanOverlayPipelines Create(Vk vk, Device device, Format targetFormat)
    {
        var pipelines = new VulkanOverlayPipelines(vk, device);
        try
        {
            pipelines.Build(targetFormat);
            return pipelines;
        }
        catch
        {
            pipelines.Dispose();
            throw;
        }
    }

    /// <summary>Creates a framebuffer for a target view; the caller owns and destroys it.</summary>
    public Framebuffer CreateFramebuffer(ImageView targetView, uint width, uint height)
    {
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 1,
            PAttachments = &targetView,
            Width = width,
            Height = height,
            Layers = 1,
        };
        Framebuffer framebuffer;
        Check(_vk.CreateFramebuffer(_device, &info, null, &framebuffer), "vkCreateFramebuffer");
        return framebuffer;
    }

    /// <summary>
    /// Records the render pass: wash over the whole target, then the CDG overlay. The target must
    /// be in <see cref="ImageLayout.ColorAttachmentOptimal"/> and stays there; the CDG image (when
    /// drawn) must be in <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.
    /// </summary>
    public void Record(CommandBuffer commandBuffer, Framebuffer framebuffer, in VulkanFramePlan plan, ImageView cdgView)
    {
        if (plan.Cdg is not null)
        {
            // Rewritten every frame: the previous submission has completed (fence wait), and
            // handle values may be reused after the CDG image is re-created.
            var imageInfo = new DescriptorImageInfo(_sampler, cdgView, ImageLayout.ShaderReadOnlyOptimal);
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _cdgDescriptorSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo,
            };
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        }

        var width = (uint)plan.OutputWidth;
        var height = (uint)plan.OutputHeight;
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
        };
        _vk.CmdBeginRenderPass(commandBuffer, &begin, SubpassContents.Inline);

        var push = stackalloc float[4];
        if (plan.WashArgb is { } wash)
        {
            push[0] = (byte)(wash >> 16) / 255f;
            push[1] = (byte)(wash >> 8) / 255f;
            push[2] = (byte)wash / 255f;
            push[3] = (byte)(wash >> 24) / 255f;
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _washPipeline);
            SetRegion(commandBuffer, new PixelRect(0, 0, plan.OutputWidth, plan.OutputHeight));
            _vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, push);
            _vk.CmdDraw(commandBuffer, 4, 1, 0, 0);
        }

        if (plan.Cdg is { Quad: var quad })
        {
            (push[0], push[1], push[2], push[3]) = quad.TexCoords;
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _cdgPipeline);
            DescriptorSet set = _cdgDescriptorSet;
            _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
            SetRegion(commandBuffer, quad.Destination);
            _vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, push);
            _vk.CmdDraw(commandBuffer, 4, 1, 0, 0);
        }

        _vk.CmdEndRenderPass(commandBuffer);
    }

    public void Dispose()
    {
        if (_cdgPipeline.Handle != 0)
            _vk.DestroyPipeline(_device, _cdgPipeline, null);
        if (_washPipeline.Handle != 0)
            _vk.DestroyPipeline(_device, _washPipeline, null);
        if (_layout.Handle != 0)
            _vk.DestroyPipelineLayout(_device, _layout, null);
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        if (_setLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        if (_sampler.Handle != 0)
            _vk.DestroySampler(_device, _sampler, null);
        if (_renderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _renderPass, null);
        _cdgPipeline = default;
        _washPipeline = default;
        _layout = default;
        _descriptorPool = default;
        _cdgDescriptorSet = default;
        _setLayout = default;
        _sampler = default;
        _renderPass = default;
    }

    private void Build(Format targetFormat)
    {
        CreateRenderPass(targetFormat);

        // Nearest + clamp-to-edge: the planner's texture coordinates put every destination pixel
        // centre inside the software renderer's source pixel; clamping covers the clipped edges.
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        };
        Sampler sampler;
        Check(_vk.CreateSampler(_device, &samplerInfo, null, &sampler), "vkCreateSampler");
        _sampler = sampler;

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        DescriptorSetLayout setLayout;
        Check(_vk.CreateDescriptorSetLayout(_device, &setLayoutInfo, null, &setLayout), "vkCreateDescriptorSetLayout");
        _setLayout = setLayout;

        var pushRange = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize);
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        PipelineLayout layout;
        Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &poolSize };
        DescriptorPool pool;
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, &pool), "vkCreateDescriptorPool");
        _descriptorPool = pool;

        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        DescriptorSet set;
        Check(_vk.AllocateDescriptorSets(_device, &allocateInfo, &set), "vkAllocateDescriptorSets");
        _cdgDescriptorSet = set;

        ShaderModule vertex = default;
        ShaderModule wash = default;
        ShaderModule cdg = default;
        try
        {
            vertex = CreateShaderModule("quad.vert");
            wash = CreateShaderModule("wash.frag");
            cdg = CreateShaderModule("cdg.frag");
            _washPipeline = CreatePipeline(vertex, wash);
            _cdgPipeline = CreatePipeline(vertex, cdg);
        }
        finally
        {
            // Pipelines keep their own copy of the code; modules are not needed afterwards.
            if (cdg.Handle != 0)
                _vk.DestroyShaderModule(_device, cdg, null);
            if (wash.Handle != 0)
                _vk.DestroyShaderModule(_device, wash, null);
            if (vertex.Handle != 0)
                _vk.DestroyShaderModule(_device, vertex, null);
        }
    }

    private void CreateRenderPass(Format targetFormat)
    {
        // Load/store keeps the cleared and blitted target; explicit pipeline barriers outside the
        // pass move it between transfer and colour-attachment layouts.
        var attachment = new AttachmentDescription
        {
            Format = targetFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };
        var colorReference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorReference,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        RenderPass renderPass;
        Check(_vk.CreateRenderPass(_device, &info, null, &renderPass), "vkCreateRenderPass");
        _renderPass = renderPass;
    }

    private VkPipeline CreatePipeline(ShaderModule vertex, ShaderModule fragment)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = entryPoint,
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleStrip,
            };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            // Premultiplied source over premultiplied target, for colour and alpha alike.
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamicState,
                Layout = _layout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            VkPipeline pipeline;
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    private ShaderModule CreateShaderModule(string name)
    {
        uint[] code = LoadSpirv(name);
        fixed (uint* pCode = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(code.Length * sizeof(uint)),
                PCode = pCode,
            };
            ShaderModule module;
            Check(_vk.CreateShaderModule(_device, &info, null, &module), "vkCreateShaderModule");
            return module;
        }
    }

    /// <summary>Viewport and scissor both cover <paramref name="region"/> (integer pixel edges).</summary>
    private void SetRegion(CommandBuffer commandBuffer, PixelRect region)
    {
        var viewport = new Viewport(region.Left, region.Top, region.Width, region.Height, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(region.Left, region.Top), new Extent2D((uint)region.Width, (uint)region.Height));
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
    }

    private static uint[] LoadSpirv(string name)
    {
        var resource = $"FatzBomb.MediaControl.Rendering.Vulkan.Shaders.{name}.spv";
        using Stream stream = typeof(VulkanOverlayPipelines).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded SPIR-V '{resource}' is missing.");
        if (stream.Length == 0 || stream.Length % sizeof(uint) != 0)
            throw new InvalidOperationException($"Embedded SPIR-V '{resource}' is not a whole number of 32-bit words.");

        // uint[] keeps the code 4-byte aligned as vkCreateShaderModule requires.
        var code = new uint[stream.Length / sizeof(uint)];
        stream.ReadExactly(MemoryMarshal.AsBytes(code.AsSpan()));
        return code;
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new VulkanException(operation, result);
    }
}
