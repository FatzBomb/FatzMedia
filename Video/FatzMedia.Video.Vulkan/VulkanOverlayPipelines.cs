using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// Graphics pipelines drawn after the clear and background blit: a full-target wash, the
/// nearest-sampled CDG overlay and the nearest-sampled subtitle raster. All blend premultiplied
/// colour ONE / ONE_MINUS_SRC_ALPHA into
/// the BGRA8 target inside one render pass that loads and stores the target. SPIR-V is compiled
/// from <c>Shaders/*.vert|*.frag</c> (scripts/compile-vulkan-shaders.sh) and embedded.
/// </summary>
sealed unsafe class VulkanOverlayPipelines : IDisposable
{
    // One vec4 of push constants at offset 0, visible to both stages: the vertex stage reads it as
    // the CDG or subtitle texture rectangle, the wash fragment stage as the wash colour (the wash quad ignores
    // texture coordinates). Keeping every read at offset 0 also sidesteps a Mesa lavapipe
    // (LLVM 20) issue where a fragment-stage vec4 push-constant load at offset 16 returned the
    // bytes at offset 0.
    const uint PushConstantSize = 4 * sizeof(float);

    readonly Vk _vk;
    readonly Device _device;
    RenderPass _renderPass;
    DescriptorSetLayout _setLayout;
    PipelineLayout _layout;
    DescriptorPool _descriptorPool;
    DescriptorSet _cdgDescriptorSet;
    DescriptorSet _subtitleDescriptorSet;
    Sampler _sampler;
    VkPipeline _washPipeline;
    VkPipeline _cdgPipeline;
    VkPipeline _subtitlePipeline;

    VulkanOverlayPipelines(Vk vk, Device device)
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
    public Framebuffer CreateFramebuffer(ImageView targetView, uint width, uint height) =>
        CreateFramebuffer(_vk, _device, _renderPass, targetView, width, height, "vkCreateFramebuffer");

    /// <summary>
    /// Creates a single-attachment, single-layer framebuffer of <paramref name="renderPass"/> over
    /// <paramref name="view"/>; <paramref name="operation"/> names the step in a failure.
    /// </summary>
    internal static Framebuffer CreateFramebuffer(Vk vk, Device device, RenderPass renderPass, ImageView view, uint width, uint height, string operation)
    {
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = width,
            Height = height,
            Layers = 1,
        };
        Framebuffer framebuffer;
        VulkanResult.Check(vk.CreateFramebuffer(device, &info, null, &framebuffer), operation);
        return framebuffer;
    }

    /// <summary>
    /// Records the render pass: wash over the whole target, then the CDG overlay, then the
    /// subtitle raster (the software renderer's order). The target must be in
    /// <see cref="ImageLayout.ColorAttachmentOptimal"/> and stays there; the CDG and subtitle
    /// images (when drawn) must be in <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.
    /// </summary>
    public void Record(CommandBuffer commandBuffer, Framebuffer framebuffer, in VulkanFramePlan plan, ImageView cdgView, VkBuffer cdgMap, ImageView subtitleView)
    {
        // Rewritten every frame: the previous submission has completed (fence wait), and handle
        // values may be reused after an image is re-created.
        if (plan.Cdg is not null)
        {
            UpdateImageDescriptor(_cdgDescriptorSet, cdgView);
            UpdateMapDescriptor(_cdgDescriptorSet, cdgMap);
        }
        if (plan.Subtitle is not null)
            UpdateImageDescriptor(_subtitleDescriptorSet, subtitleView);

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
            // The CDG shader maps each pixel through cdgMap (ENG-049): it needs the clipped
            // destination's origin and its column and row counts, not texture coordinates.
            var destination = quad.Destination;
            (push[0], push[1], push[2], push[3]) = (destination.Left, destination.Top, destination.Right - destination.Left, destination.Bottom - destination.Top);
            DrawQuad(commandBuffer, _cdgPipeline, _cdgDescriptorSet, destination, push);
        }

        if (plan.Subtitle is { Quad: var subtitleQuad })
            DrawTexturedQuad(commandBuffer, _subtitlePipeline, _subtitleDescriptorSet, subtitleQuad, push);

        _vk.CmdEndRenderPass(commandBuffer);
    }

    void UpdateImageDescriptor(DescriptorSet set, ImageView view)
    {
        var imageInfo = new DescriptorImageInfo(_sampler, view, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    void DrawTexturedQuad(CommandBuffer commandBuffer, VkPipeline pipeline, DescriptorSet set, in VulkanOverlayQuad quad, float* push)
    {
        (push[0], push[1], push[2], push[3]) = quad.TexCoords;
        DrawQuad(commandBuffer, pipeline, set, quad.Destination, push);
    }

    void DrawQuad(CommandBuffer commandBuffer, VkPipeline pipeline, DescriptorSet set, PixelRect destination, float* push)
    {
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        SetRegion(commandBuffer, destination);
        _vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, push);
        _vk.CmdDraw(commandBuffer, 4, 1, 0, 0);
    }

    void UpdateMapDescriptor(DescriptorSet set, VkBuffer buffer)
    {
        var bufferInfo = new DescriptorBufferInfo(buffer, 0, Vk.WholeSize);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &bufferInfo,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        if (_subtitlePipeline.Handle != 0)
            _vk.DestroyPipeline(_device, _subtitlePipeline, null);
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
        _subtitlePipeline = default;
        _cdgPipeline = default;
        _washPipeline = default;
        _layout = default;
        _descriptorPool = default;
        _cdgDescriptorSet = default;
        _subtitleDescriptorSet = default;
        _setLayout = default;
        _sampler = default;
        _renderPass = default;
    }

    void Build(Format targetFormat)
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
        VulkanResult.Check(_vk.CreateSampler(_device, &samplerInfo, null, &sampler), "vkCreateSampler");
        _sampler = sampler;

        // Binding 1, the CDG source map (ENG-049), is used only by the CDG shader; the subtitle set
        // leaves it unwritten, which is allowed for a binding its shader does not use.
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        DescriptorSetLayout setLayout;
        VulkanResult.Check(_vk.CreateDescriptorSetLayout(_device, &setLayoutInfo, null, &setLayout), "vkCreateDescriptorSetLayout");
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
        VulkanResult.Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;

        // One set for the CDG texture and one for the subtitle texture.
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, 2),
            new(DescriptorType.StorageBuffer, 2),
        };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 2, PoolSizeCount = 2, PPoolSizes = poolSizes };
        DescriptorPool pool;
        VulkanResult.Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, &pool), "vkCreateDescriptorPool");
        _descriptorPool = pool;

        var setLayouts = stackalloc DescriptorSetLayout[] { setLayout, setLayout };
        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 2, PSetLayouts = setLayouts };
        var sets = stackalloc DescriptorSet[2];
        VulkanResult.Check(_vk.AllocateDescriptorSets(_device, &allocateInfo, sets), "vkAllocateDescriptorSets");
        _cdgDescriptorSet = sets[0];
        _subtitleDescriptorSet = sets[1];

        ShaderModule vertex = default;
        ShaderModule wash = default;
        ShaderModule cdg = default;
        ShaderModule subtitle = default;
        try
        {
            vertex = CreateShaderModule(_vk, _device, "quad.vert");
            wash = CreateShaderModule(_vk, _device, "wash.frag");
            cdg = CreateShaderModule(_vk, _device, "cdg.frag");
            subtitle = CreateShaderModule(_vk, _device, "subtitle.frag");
            _washPipeline = CreateGraphicsPipeline(_vk, _device, _layout, _renderPass, vertex, wash, blend: true);
            _cdgPipeline = CreateGraphicsPipeline(_vk, _device, _layout, _renderPass, vertex, cdg, blend: true);
            _subtitlePipeline = CreateGraphicsPipeline(_vk, _device, _layout, _renderPass, vertex, subtitle, blend: true);
        }
        finally
        {
            // Pipelines keep their own copy of the code; modules are not needed afterwards.
            if (subtitle.Handle != 0)
                _vk.DestroyShaderModule(_device, subtitle, null);
            if (cdg.Handle != 0)
                _vk.DestroyShaderModule(_device, cdg, null);
            if (wash.Handle != 0)
                _vk.DestroyShaderModule(_device, wash, null);
            if (vertex.Handle != 0)
                _vk.DestroyShaderModule(_device, vertex, null);
        }
    }

    void CreateRenderPass(Format targetFormat)
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
        VulkanResult.Check(_vk.CreateRenderPass(_device, &info, null, &renderPass), "vkCreateRenderPass");
        _renderPass = renderPass;
    }

    /// <summary>
    /// A triangle-strip pipeline for <c>quad.vert</c> with dynamic viewport and scissor, blending
    /// premultiplied colour ONE / ONE_MINUS_SRC_ALPHA when <paramref name="blend"/> (otherwise
    /// writing every channel as is).
    /// </summary>
    internal static VkPipeline CreateGraphicsPipeline(
        Vk vk,
        Device device,
        PipelineLayout layout,
        RenderPass renderPass,
        ShaderModule vertex,
        ShaderModule fragment,
        bool blend)
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
                BlendEnable = blend,
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
                Layout = layout,
                RenderPass = renderPass,
                Subpass = 0,
            };
            VkPipeline pipeline;
            VulkanResult.Check(vk.CreateGraphicsPipelines(device, default, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    /// <summary>Creates a module from embedded SPIR-V (<c>Shaders/&lt;name&gt;.spv</c>); the caller destroys it.</summary>
    internal static ShaderModule CreateShaderModule(Vk vk, Device device, string name)
    {
        var code = LoadSpirv(name);
        fixed (uint* pCode = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(code.Length * sizeof(uint)),
                PCode = pCode,
            };
            ShaderModule module;
            VulkanResult.Check(vk.CreateShaderModule(device, &info, null, &module), "vkCreateShaderModule");
            return module;
        }
    }

    /// <summary>Viewport and scissor both cover <paramref name="region"/> (integer pixel edges).</summary>
    void SetRegion(CommandBuffer commandBuffer, PixelRect region)
    {
        var viewport = new Viewport(region.Left, region.Top, region.Width, region.Height, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(region.Left, region.Top), new Extent2D((uint)region.Width, (uint)region.Height));
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
    }

    static uint[] LoadSpirv(string name)
    {
        var resource = $"FatzMedia.Video.Vulkan.Shaders.{name}.spv";
        using var stream = typeof(VulkanOverlayPipelines).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded SPIR-V '{resource}' is missing.");
        if (stream.Length == 0 || stream.Length % sizeof(uint) != 0)
            throw new InvalidOperationException($"Embedded SPIR-V '{resource}' is not a whole number of 32-bit words.");

        // uint[] keeps the code 4-byte aligned as vkCreateShaderModule requires.
        var code = new uint[stream.Length / sizeof(uint)];
        stream.ReadExactly(MemoryMarshal.AsBytes(code.AsSpan()));
        return code;
    }
}
