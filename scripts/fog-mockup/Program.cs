using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using VintageHorizons.Tools;

// Offline mock-up of the Farseer seam fog, so a fog change can be judged before a build.
//
//   dotnet run --project scripts/fog-mockup -c Release -- [OUT_DIR] [TILE_WIDTH]
//       [--with-0.4.122] [--with-0.4.124] [--ramp=BLOCKS]
//
// Writes OUT_DIR/fog-mockup.png (default OUT_DIR fog-mockup-out, TILE_WIDTH 640): one row per
// viewpoint - above, at a distance, at ground level, from the Farseer side, along the seam and
// straight down - and one column each for no fog and the shipped shader on its defaults, plus
// 0.4.124 and 0.4.122 on the owner's dials of their day when asked. It prints, and labels, how much of the exact staircase still shows
// through (average and worst pixel) and how much of the terrain beyond the fog does.
//
// OUT_DIR/fog-dissolve.png shows the seam moving 64 blocks toward Farseer, as cached terrain
// loading in moves it: the fog before the move, then the old fog dissolving into the new at
// 0, 25, 50, 75 and 100% of the fade time.
//
// --ramp=BLOCKS tilts the ground across the seam by up to that many blocks either side, so the
// real ground at the staircase is off the sampled seam height as on a hillside, and adds a
// column with the strip resting on the sampled height alone.
//
// The mock-up marches at full resolution; the game marches at half and upsamples. Session 107
// introduced it; 0.4.125 added the hills, the ramp, the dissolve and the see-through count.
//
// What it is not: the game. The ground is simple, the sun fixed, colours are display values
// without the engine's tone mapping or bloom, and the field is analytic rather than traced from
// a real frontier. It shows the fog's shape, falloff and coverage, not its final colour; the
// owner's in-game look remains the acceptance test.

string repo = FindRepository();
bool with0122 = args.Contains("--with-0.4.122");
bool with0124 = args.Contains("--with-0.4.124");
string? rampArg = args.FirstOrDefault(a => a.StartsWith("--ramp=", StringComparison.Ordinal));
Scene.Ramp = rampArg == null ? 0f : float.Parse(rampArg["--ramp=".Length..], CultureInfo.InvariantCulture);
args = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
string outDir = args.Length > 0 ? args[0] : "fog-mockup-out";
int tileWidth = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 640;
int tileHeight = tileWidth * 9 / 16;
Directory.CreateDirectory(outDir);

string shader = File.ReadAllText(Path.Combine(repo,
    "VintageHorizons", "assets", "vintagehorizons", "shaders", "lodsilhouettefogvolume.fsh"));
string volume = File.ReadAllText(Path.Combine(repo,
    "VintageHorizons", "src", "Render", "LodSilhouetteFogVolume.cs"));
float Default(string name) => float.Parse(Regex.Match(volume,
        @"public const float Default" + name + @" = ([\d.]+)f;").Groups[1].Value,
    CultureInfo.InvariantCulture);
float Const(string name) => float.Parse(Regex.Match(volume,
        @"public const float " + name + @" = ([\d.]+)f;").Groups[1].Value,
    CultureInfo.InvariantCulture);

// The shipped defaults are the owner's tuning of 0.4.125 (2026-09-21).
var shipped = new FogDials(Default("Strength"), Default("Coverage"), Default("SpillBlocks"),
    Default("HeightBlocks"), Default("FloorDepthBlocks"), Default("BillowSizeBlocks"),
    Default("EdgeSoftness"), Default("LayerDetail"), DriftBlocks: 37f,
    SeamStrength: Default("SeamStrength"), StripHeight: Default("StripHeightBlocks"));
// The owner's saved dials after 0.4.124 (2026-09-21): 60/60, spread 48, height 32, floor 20,
// edge 0.35, coverage 1, billows 12, quality 0.52.
var dials0124 = new FogDials(0.6f, 1f, 48f, 32f, 20f, 12f, 0.35f, 0.52f, DriftBlocks: 37f,
    SeamStrength: 0.6f);
// The owner's saved dials when 0.4.121 and 0.4.122 were photographed (2026-09-20 15:57).
var dials0122 = new FogDials(1.25f, 0.9f, 40f, 32f, 26f, 32f, 0.2f, 0.75f, DriftBlocks: 37f,
    SeamStrength: 1.25f);

float deviation = Scene.StaircaseDeviation(-2500f, 2500f);
float core = Math.Clamp(deviation + Const("CoreMarginBlocks"), Const("MinCoreBlocks"),
    Const("MaxCoreBlocks"));
float stripHalf = Math.Clamp(deviation + Const("StripMarginBlocks"), Const("MinStripHalfBlocks"),
    Const("MaxStripHalfBlocks"));
var noise = new NoiseTexture();
var fog = new ShippedFog(shader, shipped, noise, core, stripHalf);
var fog0124 = new Fog0124(dials0124, noise, core);
Console.WriteLine($"staircase strays up to {deviation:0.0} blocks; bank core {core:0.0} and strip "
    + $"{stripHalf:0.0} each side; ramp {Scene.Ramp:0} blocks");

float G = Scene.GroundY;
var views = new[]
{
    new View("from above", new Vector3(0f, G + 260f, 120f), 0f, -38f, 55f),
    new View("at a distance", new Vector3(0f, G + 140f, -260f), 0f, -14f, 45f),
    new View("ground level", new Vector3(0f, G + 10f, 330f), 0f, -1.5f, 60f),
    new View("from the farseer side", new Vector3(60f, G + 45f, 860f), 180f, -5f, 55f),
    new View("along the seam", new Vector3(-700f, G + 30f, Scene.CentreZ(-700f) - 60f), 90f, -4f, 55f),
    new View("straight down", new Vector3(0f, G + 320f, Scene.CentreZ(0f)), 0f, -89.5f, 60f),
};
var current = new FogFields(Scene.SignedNow);
var columns = new List<(string Name, Marcher March)>
{
    ("no fog", (c, d, e, l, x, y, p) => default),
    ("0.4.125, shipped defaults", (c, d, e, l, x, y, p) => fog.March(c, d, e, l, x, y, p, current)),
};
if (Scene.Ramp != 0f)
    columns.Add(("0.4.125, strip on sampled height",
        (c, d, e, l, x, y, p) => fog.March(c, d, e, l, x, y, p, current, anchor: false)));
if (with0124)
    columns.Insert(1, ("0.4.124, owner's 60/60", (c, d, e, l, x, y, p) => fog0124.March(c, d, e, x, y, p)));
if (with0122)
    columns.Insert(1, ("0.4.122, old dials", (c, d, e, l, x, y, p) => Fog0122.March(dials0122, c, d, e, x, y)));

const int gap = 8, labelHeight = 72, headerHeight = 64;
var sheet = new Image(columns.Count * tileWidth + (columns.Count + 1) * gap,
    headerHeight + views.Length * (tileHeight + labelHeight + gap) + gap, fill: 28);
sheet.Text(gap, 10, "seam fog mock-up: one 32-block staircase seam, farseer hills behind, fixed sun.", 3, 235);
sheet.Text(gap, 38, $"seam: how much of the staircase shows through. beyond: how much of the terrain "
    + $"past the fog does. strip {stripHalf:0} blocks each side.", 2, 200);
for (int row = 0; row < views.Length; row++)
{
    for (int column = 0; column < columns.Count; column++)
    {
        Tile tile = Render(views[row], tileWidth, tileHeight, columns[column].March, core);
        int ox = gap + column * (tileWidth + gap);
        int oy = headerHeight + row * (tileHeight + labelHeight + gap);
        sheet.Text(ox, oy + 6, $"{columns[column].Name}, {views[row].Name}", 2, 235);
        if (column > 0 && tile.SeamCount > 0)
            sheet.Text(ox, oy + 26, $"seam shows: avg {tile.SeamMean * 100f:0}%, worst {tile.SeamWorst * 100f:0}%",
                2, tile.SeamMean > 0.05f || tile.SeamWorst > 0.15f ? (byte)150 : (byte)235);
        if (column > 0 && tile.BeyondCount > 0)
            sheet.Text(ox, oy + 46, $"terrain beyond the fog shows: avg {tile.BeyondMean * 100f:0}%", 2, 235);
        sheet.Blit(tile.Picture, ox, oy + labelHeight);
        Console.WriteLine($"{views[row].Name,-22} {columns[column].Name,-34} seam {tile.SeamCount,6} px, "
            + $"shows avg {tile.SeamMean * 100f,5:0.0}%, worst {tile.SeamWorst * 100f,5:0.0}%{tile.Where}; "
            + $"beyond {tile.BeyondCount,6} px, shows avg {tile.BeyondMean * 100f,5:0.0}%");
    }
}
string path = Path.Combine(outDir, "fog-mockup.png");
sheet.WritePng(path);
Console.WriteLine(path);

// The dissolve: the frontier moves 64 blocks toward Farseer. The current field follows it and
// the old one dissolves; its weight is what the shader reads as alpha minus elapsed fade.
const float moveBlocks = 64f;
View[] dissolveViews = { views[1], views[0], views[2] };
float[] progress = { -1f, 0f, 0.25f, 0.5f, 0.75f, 1f };
int small = Math.Max(240, tileWidth / 2), smallHeight = small * 9 / 16;
var dissolve = new Image(progress.Length * small + (progress.Length + 1) * gap,
    headerHeight + dissolveViews.Length * (smallHeight + 32 + gap) + gap, fill: 28);
dissolve.Text(gap, 10, $"the seam moves {moveBlocks:0} blocks toward farseer; the old fog dissolves.", 3, 235);
dissolve.Text(gap, 38, "left: before the move. then 0, 25, 50, 75 and 100% of the fade time.", 2, 200);
for (int row = 0; row < dissolveViews.Length; row++)
{
    for (int column = 0; column < progress.Length; column++)
    {
        float p = progress[column];
        Scene.Shift = p < 0f ? 0f : moveBlocks;
        var fields = p < 0f
            ? new FogFields(Scene.SignedNow)
            : new FogFields(Scene.SignedNow, Scene.Signed, 1f - p);
        Tile tile = Render(dissolveViews[row], small, smallHeight,
            (c, d, e, l, x, y, px) => fog.March(c, d, e, l, x, y, px, fields), core);
        int ox = gap + column * (small + gap);
        int oy = headerHeight + row * (smallHeight + 32 + gap);
        string label = p < 0f ? "before" : $"{p * 100f:0}% faded";
        dissolve.Text(ox, oy + 8, $"{label}, {dissolveViews[row].Name}", 2, 235);
        dissolve.Blit(tile.Picture, ox, oy + 32);
        Console.WriteLine($"dissolve {dissolveViews[row].Name,-14} {label,-12} seam shows avg "
            + $"{tile.SeamMean * 100f,5:0.0}%, worst {tile.SeamWorst * 100f,5:0.0}%");
    }
}
Scene.Shift = 0f;
path = Path.Combine(outDir, "fog-dissolve.png");
dissolve.WritePng(path);
Console.WriteLine(path);

static Tile Render(View view, int w, int h, Marcher march, float core)
{
    var image = new Image(w, h);
    var leaks = new System.Collections.Concurrent.ConcurrentBag<(float Leak, int X, int Y, float Hit)>();
    var beyond = new System.Collections.Concurrent.ConcurrentBag<float>();
    float yaw = view.YawDeg * MathF.PI / 180f, pitch = view.PitchDeg * MathF.PI / 180f;
    var forward = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch),
        MathF.Cos(yaw) * MathF.Cos(pitch));
    var right = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
    Vector3 up = Vector3.Cross(forward, right);
    float tanHalf = MathF.Tan(view.VFovDeg * MathF.PI / 360f);
    float aspect = w / (float)h;
    float pixelScale = 2f * tanHalf / h;
    float cameraSide = Scene.SignedNow(view.Camera.X, view.Camera.Z);
    Parallel.For(0, h, py =>
    {
        for (int px = 0; px < w; px++)
        {
            float ndcX = (px + 0.5f) / w * 2f - 1f;
            float ndcY = 1f - (py + 0.5f) / h * 2f;
            Vector3 dir = Vector3.Normalize(forward + right * (ndcX * tanHalf * aspect)
                + up * (ndcY * tanHalf));
            float hit = Scene.Intersect(view.Camera, dir, Scene.FarViewDistance);
            bool lands = float.IsFinite(hit) && hit < Scene.FarViewDistance;
            float endpoint = MathF.Min(hit, Scene.FarViewDistance);
            Vector3 background;
            bool edge = false, past = false;
            if (lands)
            {
                Vector3 p = view.Camera + dir * hit;
                background = Scene.Ground(p.X, p.Z, hit);
                bool here = Scene.Farseer(p.X, p.Z);
                edge = here != Scene.Farseer(p.X + 3f, p.Z) || here != Scene.Farseer(p.X - 3f, p.Z)
                    || here != Scene.Farseer(p.X, p.Z + 3f) || here != Scene.Farseer(p.X, p.Z - 3f);
                // Terrain on the far side of the fog from the camera, clear of the bank itself.
                float side = Scene.SignedNow(p.X, p.Z);
                past = cameraSide < -core ? side > core + 40f : cameraSide > core && side < -core - 40f;
            }
            else background = Scene.Sky(dir);
            Fog f = march(view.Camera, dir, endpoint, lands, px, py, pixelScale);
            if (edge) leaks.Add((1f - f.Alpha, px, py, hit));
            if (past) beyond.Add(1f - f.Alpha);
            Vector3 colour = background * (1f - f.Alpha) + f.Colour * f.Alpha;
            int at = (py * w + px) * 3;
            image.Rgb[at] = ToByte(colour.X);
            image.Rgb[at + 1] = ToByte(colour.Y);
            image.Rgb[at + 2] = ToByte(colour.Z);
        }
    });
    var sorted = leaks.OrderBy(v => v.Leak).ToArray();
    float beyondMean = beyond.IsEmpty ? 0f : beyond.Average();
    if (sorted.Length == 0) return new Tile(image, 0f, 0f, 0, "", beyondMean, beyond.Count);
    var top = sorted[^1];
    return new Tile(image, sorted.Average(v => v.Leak), top.Leak, sorted.Length,
        $" (worst at pixel {top.X},{top.Y}, {top.Hit:0} blocks away)", beyondMean, beyond.Count);
}

static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

static string FindRepository()
{
    for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "VintageHorizons", "VintageHorizons.csproj"))) return dir;
    for (string? dir = Directory.GetCurrentDirectory(); dir != null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "VintageHorizons", "VintageHorizons.csproj"))) return dir;
    throw new InvalidOperationException("run this from inside the Vintage Horizons repository");
}

/// <summary>One ray's fog: camera, direction, distance to its end, whether it lands, pixel.</summary>
delegate Fog Marcher(Vector3 camera, Vector3 direction, float endpoint, bool lands, int px, int py,
    float pixelScale);

record Tile(Image Picture, float SeamMean, float SeamWorst, int SeamCount, string Where,
    float BeyondMean, int BeyondCount);
