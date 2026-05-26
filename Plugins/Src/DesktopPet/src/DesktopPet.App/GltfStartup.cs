using DesktopPet.Configuration;
using DesktopPet.Models.Gltf;
using DesktopPet.Rendering.D3D11;

internal static class GltfStartup
{
    public static void TryLogSelectedModel(string[] args, DesktopPetLogger logger)
    {
        var selectedModel = ReadArgumentValue(args, "--gltf-model");
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return;
        }

        try
        {
            var loader = new GltfModelLoader();
            var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "gltf", "models");
            var modelPath = ResolveModelPath(selectedModel, modelsDirectory, loader);
            var model = loader.Load(modelPath);

            logger.Info(
                $"Loaded glTF model '{model.Summary.Name}': meshes={model.Summary.MeshCount}, materials={model.Summary.MaterialCount}, animations={model.Summary.AnimationCount}, vrm={model.Summary.HasVrmExtension}.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to load glTF model {selectedModel}");
        }
    }

    public static GltfMeshSubmissionLoop? TryCreateMeshSubmissionLoop(string[] args, IRenderHost renderHost, DesktopPetLogger logger)
    {
        var selectedModel = ReadArgumentValue(args, "--gltf-model");
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return null;
        }

        var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "gltf", "models");
        return TryCreateMeshSubmissionLoopByName(selectedModel, modelsDirectory, renderHost, logger);
    }

    /// <summary>
    /// 按模型名称（不含扩展名）从指定目录加载 GLB/glTF 模型并创建提交循环。
    /// 供运行时托盘菜单热切换使用。
    /// </summary>
    public static GltfMeshSubmissionLoop? TryCreateMeshSubmissionLoopByName(
        string modelName, string modelsDirectory, IRenderHost renderHost, DesktopPetLogger logger)
    {
        try
        {
            var loader = new GltfModelLoader();
            var modelPath = ResolveModelPath(modelName, modelsDirectory, loader);
            var model = loader.Load(modelPath);
            logger.Info(
                $"Loaded glTF model '{model.Summary.Name}': meshes={model.Summary.MeshCount}, " +
                $"materials={model.Summary.MaterialCount}, animations={model.Summary.AnimationCount}, vrm={model.Summary.HasVrmExtension}.");
            return new GltfMeshSubmissionLoop(model, renderHost);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to create GLB mesh submission loop for {modelName}");
            return null;
        }
    }

    private static string ResolveModelPath(string selectedModel, string modelsDirectory, GltfModelLoader loader)
    {
        if (File.Exists(selectedModel))
        {
            return selectedModel;
        }

        var directGlbPath = Path.Combine(modelsDirectory, selectedModel, $"{selectedModel}.glb");
        if (File.Exists(directGlbPath))
        {
            return directGlbPath;
        }

        var directGltfPath = Path.Combine(modelsDirectory, selectedModel, $"{selectedModel}.gltf");
        if (File.Exists(directGltfPath))
        {
            return directGltfPath;
        }

        var match = loader.FindModelFiles(modelsDirectory)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                selectedModel,
                StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(match))
        {
            return match;
        }

        throw new FileNotFoundException($"glTF model not found: {selectedModel}");
    }

    private static string? ReadArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        var prefix = name + "=";
        return args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }
}
