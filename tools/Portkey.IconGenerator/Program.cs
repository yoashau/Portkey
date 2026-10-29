using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Portkey.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.Single());
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        foreach (var (name, color) in new[] { ("Portkey", Color.FromRgb(32, 32, 32)), ("Portkey.light", Colors.White) })
        {
            byte[] Png(int pixels)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(TrayIcon.RenderImage(TrayIcon.Link, pixels, color)));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            }
            var frames = sizes.Select(Png).ToArray();
            using var writer = new BinaryWriter(File.Create(Path.Combine(output, name + ".ico")));
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
            if (name == "Portkey") File.WriteAllBytes(Path.Combine(output, "Portkey.png"), Png(1024));
        }
        Console.WriteLine("Generated application assets using the production TrayIcon renderer.");
    }
}
