// Compile-only API surface. Never distributed or loaded. No game implementation or proprietary code.
namespace Verse
{
    public sealed class StaticConstructorOnStartup : System.Attribute { }
    public partial class Game { }
    public abstract class GameComponent
    {
        protected GameComponent() { }
        public virtual void GameComponentUpdate() { }
        public virtual void GameComponentOnGUI() { }
    }
}
