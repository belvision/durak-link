internal sealed record CardFrameRegion(string Zone, int X, int Y, int Width, int Height, float Rotation = 0)
{
    internal sealed record Reading(CardFrameRegion Region, CardCornerReader.Match Match);

    internal static IReadOnlyList<Reading> ReadAll(Bitmap frame, IEnumerable<CardFrameRegion> regions, CardCornerReader reader)
    {
        var result = new List<Reading>();
        var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
        foreach (var region in regions)
        {
            using var corner = region.Extract(frame, bounds);
            result.Add(new Reading(region, reader.Read(corner)));
        }
        return result;
    }

    internal Bitmap Extract(Bitmap frame, Rectangle bounds)
    {
            var region = this;
            var crop = new Rectangle(X, Y, Width, Height);
            if (region.Zone is not ("hand" or "table" or "trump") ||
                crop.Width <= 0 || crop.Height <= 0 || !bounds.Contains(crop) ||
                !float.IsFinite(region.Rotation) || Math.Abs(region.Rotation) > 45)
                throw new ArgumentException("Некорректная область карты для текущего кадра.");
            var corner = new Bitmap(crop.Width, crop.Height);
            using (var graphics = Graphics.FromImage(corner))
            {
                graphics.Clear(Color.White);
                graphics.TranslateTransform(crop.Width / 2f, crop.Height / 2f);
                graphics.RotateTransform(region.Rotation);
                graphics.DrawImage(frame, new RectangleF(-crop.Width / 2f, -crop.Height / 2f, crop.Width, crop.Height),
                    crop, GraphicsUnit.Pixel);
            }
            return corner;
    }
}
