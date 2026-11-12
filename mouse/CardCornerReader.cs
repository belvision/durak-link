using System.Drawing.Drawing2D;

// Input is a tightly cropped, upright rank-and-suit corner, not a full table frame.
internal sealed class CardCornerReader
{
    private const int Width = 24;
    private const int Height = 40;
    private readonly (string Code, bool[] Mask)[] templates;

    internal CardCornerReader(string directory)
    {
        templates = Directory.EnumerateFiles(directory, "*.png")
            .Select(path =>
            {
                var code = CardCodes.Parse(Path.GetFileNameWithoutExtension(path).Split('_')[0]);
                using var image = new Bitmap(path);
                return (code, Mask(image));
            }).ToArray();
        if (templates.Select(t => t.Code).Distinct().Count() != 36)
            throw new ArgumentException("Нужны эталоны всех 36 карт.");
    }

    internal record Match(string? Card, double Distance, double Margin);

    internal Match Read(Bitmap corner)
    {
        var mask = Mask(corner);
        var candidates = templates.GroupBy(t => t.Code)
            .Select(g => (Code: g.Key, Score: g.Min(t => Distance(mask, t.Mask))))
            .OrderBy(x => x.Score).Take(2).ToArray();
        var best = candidates[0];
        double margin = candidates[1].Score - best.Score;
        // Reject poor or ambiguous matches rather than guessing a card.
        return new Match(best.Score <= .24 && margin >= .07 ? best.Code : null, best.Score, margin);
    }

    private static double Distance(bool[] first, bool[] second)
    {
        int union = 0, difference = 0;
        for (int i = 0; i < first.Length; i++)
        {
            if (first[i] || second[i]) union++;
            if (first[i] != second[i]) difference++;
        }
        return union == 0 ? 1 : (double)difference / union;
    }

    private static bool[] Mask(Bitmap image)
    {
        using var normalized = new Bitmap(Width, Height);
        using var graphics = Graphics.FromImage(normalized);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(image, new Rectangle(0, 0, Width, Height));
        var mask = new bool[Width * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var color = normalized.GetPixel(x, y);
            mask[y * Width + x] = color.G < 145 && color.B < 145;
        }
        return mask;
    }
}
