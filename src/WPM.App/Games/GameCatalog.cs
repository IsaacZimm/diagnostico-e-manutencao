namespace WPM.App.Games;

/// <summary>
/// Jogo do catálogo. Valores de referência APROXIMADOS (compilados de benchmarks públicos) usados só
/// pela estimativa local, quando a API da HowManyFPS não está configurada:
/// GpuRef = FPS médio em 1080p/Alto numa RTX 4070 (índice 100); CpuCap = teto de FPS limitado pela CPU
/// num Ryzen 7 7700X (índice 100); EngineCap = trava de FPS do próprio jogo (0 = sem trava).
/// </summary>
public record GameInfo(string Name, string Genre, double GpuRef, double CpuCap, double EngineCap = 0);

public enum Bottleneck { None, Gpu, Cpu, Engine }

public record FpsEstimate(double Avg, double Low1, Bottleneck Limiter);

public static class GameCatalog
{
    // Catálogo local usado pela estimativa offline.
    public static readonly GameInfo[] Games =
    {
        new("Counter-Strike 2", "FPS competitivo", 340, 420),
        new("Valorant", "FPS competitivo", 600, 650),
        new("Fortnite", "Battle royale", 160, 250),
        new("League of Legends", "MOBA", 420, 480),
        new("Dota 2", "MOBA", 260, 280),
        new("Apex Legends", "Battle royale", 220, 300),
        new("PUBG: Battlegrounds", "Battle royale", 180, 220),
        new("Call of Duty: Black Ops 6", "FPS", 140, 220),
        new("Call of Duty: Warzone", "Battle royale", 130, 200),
        new("Battlefield 6", "FPS", 120, 170),
        new("Rainbow Six Siege", "FPS tático", 350, 430),
        new("Overwatch 2", "FPS heroico", 280, 380),
        new("Marvel Rivals", "FPS heroico", 110, 160),
        new("Rocket League", "Esporte", 400, 450),
        new("EA Sports FC 25", "Esporte", 220, 260),
        new("Minecraft", "Sandbox", 300, 340),
        new("GTA V", "Mundo aberto", 160, 190),
        new("Red Dead Redemption 2", "Mundo aberto", 95, 160),
        new("Cyberpunk 2077", "RPG mundo aberto", 95, 150),
        new("The Witcher 3: Wild Hunt", "RPG", 120, 160),
        new("Elden Ring", "RPG de ação", 110, 140, 60),
        new("Baldur's Gate 3", "RPG", 110, 120),
        new("Hogwarts Legacy", "RPG de ação", 90, 120),
        new("Starfield", "RPG espacial", 65, 90),
        new("Black Myth: Wukong", "Ação", 70, 140),
        new("Alan Wake 2", "Terror / ação", 60, 140),
        new("God of War Ragnarök", "Ação", 110, 170),
        new("Forza Horizon 5", "Corrida", 140, 200),
        new("Helldivers 2", "Cooperativo", 90, 110),
        new("Monster Hunter Wilds", "Ação", 60, 85),
        new("Assassin's Creed Shadows", "Ação mundo aberto", 65, 110),
        new("Borderlands 4", "Looter shooter", 55, 110),
    };

    // Desempenho relativo em jogos (raster), RTX 4070 = 100. Aproximado.
    private static readonly (string Key, double Index)[] GpuIndex =
    {
        ("RTX 5090", 265), ("RTX 5080", 175), ("RTX 5070 Ti", 150), ("RTX 5070", 118), ("RTX 5060 Ti", 95), ("RTX 5060", 85),
        ("RTX 4090", 210), ("RTX 4080 SUPER", 160), ("RTX 4080", 157), ("RTX 4070 Ti SUPER", 136), ("RTX 4070 Ti", 124),
        ("RTX 4070 SUPER", 116), ("RTX 4070", 100), ("RTX 4060 Ti", 82), ("RTX 4060", 70),
        ("RTX 3090 Ti", 135), ("RTX 3090", 125), ("RTX 3080 Ti", 122), ("RTX 3080", 112), ("RTX 3070 Ti", 92), ("RTX 3070", 86),
        ("RTX 3060 Ti", 78), ("RTX 3060", 60), ("RTX 3050", 40), ("GTX 1660", 38), ("GTX 1650", 28),
        ("RX 9070 XT", 160), ("RX 9070", 140), ("RX 7900 XTX", 170), ("RX 7900 XT", 150), ("RX 7900 GRE", 128),
        ("RX 7800 XT", 115), ("RX 7700 XT", 98), ("RX 7600 XT", 72), ("RX 7600", 70),
        ("RX 6950 XT", 125), ("RX 6800 XT", 112), ("RX 6700 XT", 82), ("RX 6600", 58),
        ("Arc B580", 80), ("Arc A770", 68),
        ("Radeon(TM) Graphics", 6), ("Radeon Graphics", 6),
    };

    // Desempenho relativo em jogos (limitado pela CPU), Ryzen 7 7700X = 100. Aproximado.
    private static readonly (string Key, double Index)[] CpuIndex =
    {
        ("9950X3D", 138), ("9800X3D", 140), ("7950X3D", 126), ("7800X3D", 128), ("5800X3D", 95), ("5700X3D", 90),
        ("9950X", 112), ("9900X", 110), ("9700X", 108), ("9600X", 104), ("7950X", 104), ("7900X", 102), ("7700X", 100),
        ("7700", 97), ("7600X", 96), ("7600", 93), ("5950X", 82), ("5900X", 81), ("5800X", 80), ("5700X", 77), ("Ryzen 7 5700 ", 70), ("5600X", 76), ("5600", 73),
        ("285K", 112), ("265K", 108), ("245K", 100), ("14900K", 116), ("14700K", 112), ("14600K", 104), ("13900K", 113),
        ("13700K", 108), ("13600K", 101), ("13400", 84), ("12900K", 98), ("12700K", 95), ("12600K", 90), ("12400", 78),
    };

    public static double? GpuPerf(string gpuName)
    {
        string model = gpuName.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (string prefix in new[] { "NVIDIA ", "GeForce ", "AMD ", "ATI ", "Intel ", "Radeon RX " })
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                model = prefix == "Radeon RX " ? model[7..] : model[prefix.Length..].TrimStart();
            }
        }

        if (model.StartsWith("Arc ", StringComparison.OrdinalIgnoreCase) && model.EndsWith(" Graphics", StringComparison.OrdinalIgnoreCase))
        {
            model = model[..^9];
        }

        model = System.Text.RegularExpressions.Regex.Replace(model, @"\s+\d+\s*GB$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return GpuIndex.Where(entry => entry.Key.Equals(model, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (double?)entry.Index).FirstOrDefault();
    }
    public static double? CpuPerf(string cpuName) => Lookup(CpuIndex, cpuName + " ");

    /// <summary>A chave mais longa contida no nome vence ("RTX 5070 Ti" antes de "RTX 5070").</summary>
    private static double? Lookup((string Key, double Index)[] table, string name)
    {
        var hit = table.Where(t => name.Contains(t.Key, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(t => t.Key.Length).FirstOrDefault();
        return hit.Key == null ? null : hit.Index;
    }

    public static double ResolutionFactor(string res) => res switch
    {
        "2560x1440" => 0.72, "3840x2160" => 0.42, _ => 1.0
    };

    public static double PresetFactor(string preset) => preset switch
    {
        "Baixo" => 1.6, "Médio" => 1.3, "Ultra" => 0.82, _ => 1.0
    };

    /// <summary>Upscaling (DLSS/FSR) só alivia a GPU; o ganho cresce com a resolução.</summary>
    public static double UpscalingFactor(string upscaling, string res) => upscaling switch
    {
        "Quality" => res == "1920x1080" ? 1.25 : 1.4,
        "Performance" => res == "1920x1080" ? 1.45 : 1.75,
        _ => 1.0
    };

    /// <summary>FPS = menor entre o teto da GPU, o teto da CPU e a trava do jogo.</summary>
    public static FpsEstimate Estimate(GameInfo g, double gpuIdx, double cpuIdx, string res, string preset, string upscaling)
    {
        double gpuFps = g.GpuRef * gpuIdx / 100 * ResolutionFactor(res) * PresetFactor(preset) * UpscalingFactor(upscaling, res);
        double cpuFps = g.CpuCap * cpuIdx / 100 * (preset == "Baixo" ? 1.1 : preset == "Ultra" ? 0.95 : 1.0);

        double fps = Math.Min(gpuFps, cpuFps);
        var limiter = gpuFps <= cpuFps ? Bottleneck.Gpu : Bottleneck.Cpu;
        if (g.EngineCap > 0 && g.EngineCap < fps) { fps = g.EngineCap; limiter = Bottleneck.Engine; }
        return new FpsEstimate(Math.Round(fps), Math.Round(fps * 0.75), limiter);
    }
}
