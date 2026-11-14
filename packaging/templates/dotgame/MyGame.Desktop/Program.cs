using System.Threading.Tasks;
using DotGame.Framework.Desktop;

namespace MyGame;

internal static class Program
{
    public static async Task Main()
    {
        var engine = new DesktopGameEngine(new() { Title = "MyGame" });
        await engine.RunAsync(new MainGame());
    }
}
