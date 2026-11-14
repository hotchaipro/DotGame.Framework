using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.WebGL;

namespace MyGame;

[SupportedOSPlatform("browser")]
internal static partial class Program
{
    public static async Task Main()
    {
        var engine = new WebGLGameEngine();
        await engine.RunAsync(new MainGame());
    }
}
