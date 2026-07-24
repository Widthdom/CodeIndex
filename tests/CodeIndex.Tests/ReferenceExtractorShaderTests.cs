using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_Cuda_EmitsKernelCallsResourcesIncludesAndTypes_Issue4737()
    {
        const string content = """
            #include <cuda_runtime.h>

            struct Params { float scale; };

            __device__ float scale_value(Params params, float value) {
                return params.scale * value;
            }

            template <typename T>
            __global__ void transform(
                Params params,
                float* output)
            {
                output[0] = scale_value(params, output[0]);
            }

            __global__ void clear(float* sameLineOutput) { sameLineOutput[0] = 0.0f; }

            void inspect(Params params, float* output) {
                output[0] = params.scale;
            }

            /*
            #include "phantom_cuda.h"
            phantom_cuda(output);
            */
            void launch(Params params, float* output) {
                transform<float><<<1, 32>>>(params, output);
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences("cuda", content);

        AssertReferencesContain(references, "import", null, "cuda_runtime.h");
        AssertReferencesContain(references, "call", "transform", "scale_value");
        AssertReferencesContain(references, "call", "launch", "transform");
        AssertReferencesContain(references, "type_reference", null, "Params");
        AssertReferencesContain(references, "resource_reference", "transform", "output");
        AssertReferencesContain(references, "resource_reference", "clear", "sameLineOutput");
        Assert.Equal(
            1,
            references.Count(reference =>
                reference.ReferenceKind == "call"
                && reference.ContainerName == "launch"
                && reference.SymbolName == "transform"));
        Assert.DoesNotContain(
            references,
            reference => reference.ReferenceKind == "resource_reference"
                && reference.ContainerName == "inspect"
                && reference.SymbolName == "output");
        AssertReferencesDoNotContain(references, "import", "phantom_cuda.h");
        AssertReferencesDoNotContain(references, "call", "phantom_cuda", "__global__");
    }

    [Fact]
    public void Extract_Glsl_EmitsCallsResourcesBindingsIncludesAndTypes_Issue4737()
    {
        const string content = """
            #include "lighting.glsl"

            struct Light { vec3 color; };
            layout(binding = 0) uniform sampler2D albedoTexture;
            layout(binding = 2)
            uniform sampler2D normalTexture;
            layout(binding = 1) uniform CameraBlock {
                mat4 view;
            } cameraData;

            vec4 applyLight(Light light) {
                return cameraData.view
                    * texture(albedoTexture, vec2(0.0))
                    * texture(normalTexture, vec2(0.0))
                    * vec4(light.color, 1.0);
            }

            /* phantom_glsl(); */
            void main() {
                applyLight(Light(vec3(1.0)));
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences("glsl", content);

        AssertReferencesContain(references, "import", null, "lighting.glsl");
        AssertReferencesContain(references, "binding", null, "albedoTexture", "normalTexture", "cameraData");
        AssertReferencesContain(
            references,
            "resource_reference",
            "applyLight",
            "albedoTexture",
            "normalTexture",
            "cameraData");
        AssertReferencesContain(references, "type_reference", null, "Light");
        AssertReferencesContain(references, "call", "main", "applyLight");
        AssertReferencesDoNotContain(references, "call", "phantom_glsl", "layout", "binding");
    }

    [Fact]
    public void Extract_Hlsl_EmitsCallsResourcesBindingsAndTypes_Issue4737()
    {
        const string content = """
            struct Surface { float2 uv; };
            Texture2D<float4> Albedo : register(t0);
            SamplerState LinearSampler : register(s0);
            cbuffer Constants : register(b0) { float4 Color; };
            tbuffer Lookup : register(t1) { float4 Tint; };

            float4 Shade(Surface surface) {
                return Albedo.Sample(LinearSampler, surface.uv) * Color * Tint;
            }

            /* phantom_hlsl(); */
            [numthreads(8, 8, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) {
                Surface surface;
                Shade(surface);
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences("hlsl", content);

        AssertReferencesContain(
            references,
            "binding",
            null,
            "Albedo",
            "LinearSampler",
            "Constants",
            "Lookup");
        AssertReferencesContain(
            references,
            "resource_reference",
            "Shade",
            "Albedo",
            "LinearSampler",
            "Color",
            "Tint");
        AssertReferencesContain(references, "type_reference", null, "Surface");
        AssertReferencesContain(references, "call", "CSMain", "Shade");
        AssertReferencesDoNotContain(references, "call", "phantom_hlsl", "register", "numthreads");
    }

    [Fact]
    public void Extract_Metal_EmitsCallsResourcesBindingsAndTypes_Issue4737()
    {
        const string content = """
            #include <metal_stdlib>
            using namespace metal;

            struct Vertex { float4 position; };

            float4 project(Vertex input) {
                return input.position;
            }

            float4 inspect(float4 tex) {
                return tex;
            }

            vertex float4 vertex_main(
                Vertex input [[stage_in]],
                texture2d<float> tex [[texture(0)]],
                sampler linearSampler [[sampler(0)]]) {
                float4 sampled = tex.sample(linearSampler, float2(0.0));
                return project(input) + sampled;
            }

            /* phantom_metal(); */
            """;

        var (_, references) = ExtractSymbolsAndReferences("metal", content);

        AssertReferencesContain(references, "import", null, "metal_stdlib");
        AssertReferencesContain(references, "binding", null, "tex", "linearSampler");
        AssertReferencesContain(references, "resource_reference", "vertex_main", "tex", "linearSampler");
        AssertReferencesContain(references, "type_reference", null, "Vertex");
        AssertReferencesContain(references, "call", "vertex_main", "project");
        Assert.DoesNotContain(
            references,
            reference => reference.ReferenceKind == "resource_reference"
                && reference.ContainerName == "inspect"
                && reference.SymbolName == "tex");
        AssertReferencesDoNotContain(references, "call", "phantom_metal", "texture", "sampler");
    }

    [Fact]
    public void Extract_Wgsl_EmitsCallsResourcesBindingsAndTypes_Issue4737()
    {
        const string content = """
            struct Camera {
                view_projection: mat4x4<f32>,
            }

            @group(0)
            @binding(0)
            var<uniform> camera: Camera;

            fn project(value: Camera) -> Camera {
                return value;
            }

            /*
            outer comment /* phantom_wgsl(); */
            still_in_comment();
            */
            @vertex
            fn vs_main(input: Camera) -> @builtin(position) vec4<f32> {
                let projected = project(camera);
                return projected.view_projection * vec4<f32>(0.0);
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences("wgsl", content);

        AssertReferencesContain(references, "binding", null, "camera");
        AssertReferencesContain(references, "resource_reference", "vs_main", "camera");
        AssertReferencesContain(references, "type_reference", null, "Camera");
        AssertReferencesContain(references, "call", "vs_main", "project");
        Assert.DoesNotContain(
            references,
            reference => reference.ReferenceKind == "call"
                && reference.SymbolName is "phantom_wgsl" or "still_in_comment" or "group" or "binding" or "builtin");
    }

    [Fact]
    public void Extract_Glsl_UsesWorkspaceDeclarationsForIncludedTypes_Issue4737()
    {
        const string includedContent = "struct SharedLight { vec3 color; };";
        const string content = """
            #include "shared.glsl"

            SharedLight loadLight(SharedLight light) {
                return light;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "glsl", content, "main.glsl");
        var includedSymbols = SymbolExtractor.Extract(2, "glsl", includedContent, "shared.glsl");
        var result = ReferenceExtractor.ExtractDetailedNormalized(
            1,
            "glsl",
            content,
            hasOversizeLine: false,
            symbols,
            path: "main.glsl",
            workspaceSymbols: includedSymbols);

        AssertReferencesContain(result.References, "type_reference", null, "SharedLight");
    }

    [Fact]
    public void Extract_Glsl_DoesNotReadUnstampedIncludeDeclarations_Issue4737()
    {
        const string includedContent = "struct StaleLight { vec3 color; };";
        const string content = """
            #include "shared.glsl"

            StaleLight loadLight(StaleLight light) {
                return light;
            }
            """;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_shader_unstamped_include");
        File.WriteAllText(Path.Combine(project.Root, "shared.glsl"), includedContent);
        var symbols = SymbolExtractor.Extract(1, "glsl", content, "main.glsl");
        var result = ReferenceExtractor.ExtractDetailedNormalized(
            1,
            "glsl",
            content,
            hasOversizeLine: false,
            symbols,
            path: "main.glsl",
            workspaceRoot: project.Root);

        AssertReferencesContain(result.References, "import", null, "shared.glsl");
        AssertReferencesDoNotContain(result.References, "type_reference", "StaleLight");
    }

    [Fact]
    public void Extract_ShaderSafetyCaps_ReportIncompleteGraphDiagnostics_Issue4737()
    {
        var previousLimits = ReferenceExtractor.SafetyLimitsForTesting;
        try
        {
            ReferenceExtractor.SafetyLimitsForTesting = new ReferenceExtractionSafetyLimits
            {
                MaxLookupSymbols = 1,
                MaxLookupLines = 100,
                MaxNamesPerLine = 2,
                MaxContainerCandidates = 100,
            };
            const string content = """
                struct First {};
                struct Second {};
                void main() { alpha beta gamma First value; }
                """;
            var symbols = SymbolExtractor.Extract(1, "glsl", content);

            var result = ReferenceExtractor.ExtractDetailed(1, "glsl", content, symbols);

            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Kind == ShaderReferenceExtractor.TrackedNameBudgetDiagnosticKind);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Kind == ShaderReferenceExtractor.LineNameBudgetDiagnosticKind);
            Assert.All(
                result.Diagnostics.Where(diagnostic =>
                    diagnostic.Kind is ShaderReferenceExtractor.TrackedNameBudgetDiagnosticKind
                        or ShaderReferenceExtractor.LineNameBudgetDiagnosticKind),
                diagnostic => Assert.True(ReferenceExtractor.IsSafetyCapDiagnosticKind(diagnostic.Kind)));
        }
        finally
        {
            ReferenceExtractor.SafetyLimitsForTesting = previousLimits;
        }
    }

    [Fact]
    public void SupportedLanguages_AdvertiseGpuReferenceAndGraphReadiness_Issue4737()
    {
        var supportedLanguages = ReferenceExtractor.GetSupportedLanguages();

        Assert.All(
            new[] { "cuda", "glsl", "hlsl", "metal", "wgsl" },
            language => Assert.Contains(language, supportedLanguages));
        Assert.True(SymbolKindCatalog.IsValidReferenceKind("binding"));
        Assert.True(SymbolKindCatalog.IsValidReferenceKind("resource_reference"));
    }
}
