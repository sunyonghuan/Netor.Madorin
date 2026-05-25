using DesktopPet.Models.Live2D;
using DesktopPet.Configuration;

internal static class Live2DStartup
{
    public static Live2DModel? TryLoadModel(string modelName, string modelsDirectory, DesktopPetLogger? logger = null)
    {
        try
        {
            var loader = new Live2DModelLoader();
            var modelJsonPath = Path.Combine(modelsDirectory, modelName, $"{modelName}.model3.json");
            if (!File.Exists(modelJsonPath))
            {
                var match = loader.FindModelJsonFiles(modelsDirectory)
                    .FirstOrDefault(p => string.Equals(
                        Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(p)),
                        modelName, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    logger?.Info($"Live2D model not found: {modelName}");
                    return null;
                }

                modelJsonPath = match;
            }

            var model = loader.Load(modelJsonPath);
            logger?.Info($"Live2D model switched: {model.Info.Name}, drawables={model.Info.DrawableCount}.");
            return model;
        }
        catch (Exception ex)
        {
            logger?.Error(ex, $"Live2D model switch failed: {modelName}");
            return null;
        }
    }

    public static Live2DModel? TryLoadDefaultModel(string[] args, DesktopPetLogger? logger = null)
    {
        try
        {
            var loader = new Live2DModelLoader();
            var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "live2d", "models");
            var modelJsonPath = ResolveModelJsonPath(args, modelsDirectory, loader);

            if (!File.Exists(modelJsonPath))
            {
                var message = $"Live2D default model missing: {modelJsonPath}";
                logger?.Info(message);
                Console.Error.WriteLine(message);
                return null;
            }

            var model = loader.Load(modelJsonPath);
            logger?.Info($"Live2D model loaded: {model.Info.Name}, drawables={model.Info.DrawableCount}, textures={model.Info.TexturePaths.Count}.");
            return model;
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "Live2D default model failed");
            Console.Error.WriteLine($"Live2D default model failed: {ex.Message}");
            return null;
        }
    }

    private static string ResolveModelJsonPath(string[] args, string modelsDirectory, Live2DModelLoader loader)
    {
        var selectedModelName = ReadArgumentValue(args, "--model");
        if (!string.IsNullOrWhiteSpace(selectedModelName))
        {
            var selectedPath = Path.Combine(modelsDirectory, selectedModelName, $"{selectedModelName}.model3.json");
            if (File.Exists(selectedPath))
            {
                return selectedPath;
            }

            var match = loader.FindModelJsonFiles(modelsDirectory)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)),
                    selectedModelName,
                    StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return Path.Combine(modelsDirectory, "Haru", "Haru.model3.json");
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

        const string separator = "=";
        var prefix = name + separator;
        return args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }
}
