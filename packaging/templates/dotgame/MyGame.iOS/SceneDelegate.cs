using Foundation;
using UIKit;
using DotGame.Framework.iOS;

namespace MyGame;

[Register("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
        {
            return;
        }

        this.Window = new UIWindow(windowScene);

        new iOSGameEngine(this.Window).Run(new MainGame());
    }
}
