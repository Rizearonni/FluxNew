using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FluxNew;

public static class TestTextureGenerator
{
    public static void GeneratePlaceholderTextures(string baseDirectory)
    {
        // Create directories
        Directory.CreateDirectory(Path.Combine(baseDirectory, "Interface", "Buttons"));
        Directory.CreateDirectory(Path.Combine(baseDirectory, "Interface", "DialogFrame"));
        Directory.CreateDirectory(Path.Combine(baseDirectory, "Interface", "Icons"));

        // 1. UI-CheckBox-Up (32x32 cyan square)
        CreatePlaceholder(
            Path.Combine(baseDirectory, "Interface", "Buttons", "UI-CheckBox-Up.tga"),
            32, 32, new Rgba32(0, 255, 255, 255) // Cyan
        );

        // 2. UI-DialogBox-Gold-Border (256x128 gold)
        CreatePlaceholder(
            Path.Combine(baseDirectory, "Interface", "DialogFrame", "UI-DialogBox-Gold-Border.tga"),
            256, 128, new Rgba32(255, 215, 0, 255) // Gold
        );

        // 3. INV_Misc_QuestionMark (64x64 purple)
        CreatePlaceholder(
            Path.Combine(baseDirectory, "Interface", "Icons", "INV_Misc_QuestionMark.tga"),
            64, 64, new Rgba32(128, 0, 128, 255) // Purple
        );

        Console.WriteLine("Generated placeholder textures:");
        Console.WriteLine("  - Interface/Buttons/UI-CheckBox-Up.tga");
        Console.WriteLine("  - Interface/DialogFrame/UI-DialogBox-Gold-Border.tga");
        Console.WriteLine("  - Interface/Icons/INV_Misc_QuestionMark.tga");
    }

    private static void CreatePlaceholder(string path, int width, int height, Rgba32 fillColor)
    {
        using var image = new Image<Rgba32>(width, height);
        
        // Fill all pixels with the specified color
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = fillColor;
            }
        }

        image.SaveAsTga(path);
        Console.WriteLine($"Created {width}x{height} texture: {Path.GetFileName(path)}");
    }
}
