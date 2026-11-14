using Android.App;
using Android.OS;
using Android.Widget;
using DotGame.Framework.Android;

namespace MyGame;

[Activity(
    Label = "MyGame",
    MainLauncher = true,
    ConfigurationChanges = AndroidGameEngine.ConfigurationChanges,
    Theme = "@style/DotGame.LaunchScreen")]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var hostView = new FrameLayout(this);
        this.SetContentView(hostView);

        new AndroidGameEngine(this, hostView).Run(new MainGame());
    }
}
