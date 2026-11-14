using Foundation;
using UIKit;

namespace MyGame;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    [Export("application:configurationForConnectingSceneSession:options:")]
    public override UISceneConfiguration GetConfiguration(
        UIApplication application,
        UISceneSession connectingSceneSession,
        UISceneConnectionOptions options)
    {
        return new UISceneConfiguration("Default Configuration", connectingSceneSession.Role)
        {
            DelegateType = typeof(SceneDelegate),
        };
    }
}
