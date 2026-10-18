using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace VibePlayer.Controls;

/// <summary>
/// A clickable row (settings menu) that screen readers and UI Automation see as a menu item they can invoke.
/// The mouse handling stays with the owner; <see cref="Invoked"/> is what an automation "click" runs.
/// </summary>
public sealed class ActionBorder : Border
{
    public Action? Invoked { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        public Peer(ActionBorder owner) : base(owner) { }

        protected override string GetClassNameCore() => nameof(ActionBorder);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;

        protected override string GetNameCore()
        {
            var name = AutomationProperties.GetName(Owner);
            return string.IsNullOrEmpty(name) ? base.GetNameCore() : name;
        }

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        void IInvokeProvider.Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            var owner = (ActionBorder)Owner;
            owner.Dispatcher.BeginInvoke(() => owner.Invoked?.Invoke());
        }
    }
}
