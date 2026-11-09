using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// The YCbCr-to-BGRA pipeline for hardware-decoded frames (Stage 045A slice 2): <c>quad.vert</c>
/// with <c>video.frag</c>, drawing a frame's luma and chroma plane views into the renderer's video
/// layer (a BGRA image at the display size) with no blending. The layer is then composed like an
/// uploaded software frame. The constants (<see cref="VulkanVideoConstants"/>) arrive in a uniform
/// buffer: fragment-stage push constants past offset 0 misread on Mesa lavapipe
/// (<see cref="VulkanOverlayPipelines"/>).
/// </summary>
sealed unsafe class VulkanVideoPipeline : IDisposable
{
    const uint PushConstantSize = 4 * sizeof(float);

    readonly Vk _vk;
    readonly Device _device;
    RenderPass _renderPass;
    DescriptorSetLayout _setLayout;
    PipelineLayout _layout;
    DescriptorPool _descriptorPool;
    DescriptorSet _set;
    Sampler _sampler;
    VkPipeline _pipeline;

    VulkanVideoPipeline(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>Creates every object, releasing the partial set on failure.</summary>
    public static VulkanVideoPipeline Create(Vk vk, Device device, Format targetFormat)
    {
        var pipeline = new VulkanVideoPipeline(vk, device);
        try
        {
            pipeline.Build(targetFormat);
            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Creates a framebuffer for a video layer view; the caller owns and destroys it.</summary>
    public Framebuffer CreateFramebuffer(ImageView view, uint width, uint height) =>
        VulkanOverlayPipelines.CreateFramebuffer(_vk, _device, _renderPass, view, width, height, "vkCreateFramebuffer (video)");

    /// <summary>
    /// Records the conversion into <paramref name="framebuffer"/> (a layer in
    /// <see cref="ImageLayout.ColorAttachmentOptimal"/>, left there). The plane views must be in
    /// <see cref="ImageLayout.ShaderReadOnlyOptimal"/>; the constants already in
    /// <paramref name="constants"/>, which the previous submission no longer reads.
    /// </summary>
    public void Record(
        CommandBuffer commandBuffer,
        Framebuffer framebuffer,
        uint width,
        uint height,
        ImageView luma,
        ImageView chroma,
        VkBuffer constants,
        in VulkanVideoConstants values)
    {
        var images = stackalloc DescriptorImageInfo[2];
        images[0] = new DescriptorImageInfo(_sampler, luma, ImageLayout.ShaderReadOnlyOptimal);
        images[1] = new DescriptorImageInfo(_sampler, chroma, ImageLayout.ShaderReadOnlyOptimal);
        var bufferInfo = new DescriptorBufferInfo(constants, 0, (ulong)sizeof(VulkanVideoConstants));
        var writes = stackalloc WriteDescriptorSet[3];
        for (var binding = 0; binding < 2; binding++)
        {
            writes[binding] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)binding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[binding],
            };
        }

        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &bufferInfo,
        };
        _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);

        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
        };
        _vk.CmdBeginRenderPass(commandBuffer, &begin, SubpassContents.Inline);
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        var set = _set;
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var viewport = new Viewport(0, 0, width, height, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height));
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        var push = stackalloc float[4] { 0f, 0f, values.TextureScale.X, values.TextureScale.Y };
        _vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit, 0, PushConstantSize, push);
        _vk.CmdDraw(commandBuffer, 4, 1, 0, 0);
        _vk.CmdEndRenderPass(commandBuffer);
    }

    public void Dispose()
    {
        if (_pipeline.Handle != 0)
            _vk.DestroyPipeline(_device, _pipeline, null);
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
        _pipeline = default;
        _layout = default;
        _descriptorPool = default;
        _set = default;
        _setLayout = default;
        _sampler = default;
        _renderPass = default;
    }

    void Build(Format targetFormat)
    {
        // Every pixel is written, so the old contents are not loaded; barriers outside the pass
        // move the layer between colour attachment and transfer source.
        var attachment = new AttachmentDescription
        {
            Format = targetFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
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
        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        RenderPass renderPass;
        VulkanResult.Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, &renderPass), "vkCreateRenderPass (video)");
        _renderPass = renderPass;

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        };
        Sampler sampler;
        VulkanResult.Check(_vk.CreateSampler(_device, &samplerInfo, null, &sampler), "vkCreateSampler (video)");
        _sampler = sampler;

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (var binding = 0; binding < 3; binding++)
        {
            bindings[binding] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)binding,
                DescriptorType = binding < 2 ? DescriptorType.CombinedImageSampler : DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var setLayoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings };
        DescriptorSetLayout setLayout;
        VulkanResult.Check(_vk.CreateDescriptorSetLayout(_device, &setLayoutInfo, null, &setLayout), "vkCreateDescriptorSetLayout (video)");
        _setLayout = setLayout;

        var pushRange = new PushConstantRange(ShaderStageFlags.VertexBit, 0, PushConstantSize);
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        PipelineLayout layout;
        VulkanResult.Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, &layout), "vkCreatePipelineLayout (video)");
        _layout = layout;

        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.UniformBuffer, 1);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 2, PPoolSizes = poolSizes };
        DescriptorPool pool;
        VulkanResult.Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, &pool), "vkCreateDescriptorPool (video)");
        _descriptorPool = pool;

        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        DescriptorSet set;
        VulkanResult.Check(_vk.AllocateDescriptorSets(_device, &allocateInfo, &set), "vkAllocateDescriptorSets (video)");
        _set = set;

        ShaderModule vertex = default;
        ShaderModule fragment = default;
        try
        {
            vertex = VulkanOverlayPipelines.CreateShaderModule(_vk, _device, "quad.vert");
            fragment = VulkanOverlayPipelines.CreateShaderModule(_vk, _device, "video.frag");
            _pipeline = VulkanOverlayPipelines.CreateGraphicsPipeline(_vk, _device, _layout, _renderPass, vertex, fragment, blend: false);
        }
        finally
        {
            if (fragment.Handle != 0)
                _vk.DestroyShaderModule(_device, fragment, null);
            if (vertex.Handle != 0)
                _vk.DestroyShaderModule(_device, vertex, null);
        }
    }
}
