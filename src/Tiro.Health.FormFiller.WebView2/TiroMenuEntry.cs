using System;

namespace Tiro.Health.FormFiller.WebView2
{
    /// <summary>
    /// What a <see cref="TiroMenuEntry"/> renders as. Mirrors the embedded browser's own kinds
    /// so a native entry survives a round trip through the host's builder unchanged.
    /// </summary>
    public enum TiroMenuEntryKind
    {
        Command = 0,
        CheckBox = 1,
        Radio = 2,
        Separator = 3,
        Submenu = 4,
    }

    /// <summary>
    /// One line in the form's right-click menu, as the host's builder sees it. An entry is
    /// either the embedded browser's own (Copy, Paste, a spelling suggestion — see
    /// <see cref="IsFromBrowser"/>), one of the host's, or a separator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A browser entry can be reordered or left out, but it cannot be invented: the only way to
    /// obtain one is to take it from <see cref="TiroContextMenuRequest.BrowserItems"/> for the
    /// click being built. The browser's entries differ per click — Copy is absent without a
    /// selection, and spelling suggestions are generated fresh, with the suggested words as
    /// their labels — so an entry kept from an earlier click is stale and is dropped.
    /// </para>
    /// <para>
    /// <see cref="Label"/> and <see cref="Kind"/> are read-only for both flavours; the browser
    /// localises its own labels (and marks the keyboard accelerator with an ampersand, so
    /// <see cref="Name"/> is the identity to match on, never the label).
    /// </para>
    /// </remarks>
    public sealed class TiroMenuEntry
    {
        /// <summary>The <see cref="Name"/> every host-supplied entry carries.</summary>
        public const string CustomName = "custom";

        /// <summary>The <see cref="Name"/> a harness-created separator carries.</summary>
        public const string SeparatorName = "separator";

        private bool _isEnabled;

        /// <summary>The browser's own entry, wrapping the object only the browser layer can make.</summary>
        internal TiroMenuEntry(string name, string label, TiroMenuEntryKind kind, bool isEnabled, object browserHandle)
        {
            Name = name;
            Label = label;
            Kind = kind;
            _isEnabled = isEnabled;
            BrowserHandle = browserHandle ?? throw new ArgumentNullException(nameof(browserHandle));
            IsFromBrowser = true;
        }

        /// <summary>A host entry. The action is already guarded by the viewer.</summary>
        internal TiroMenuEntry(string label, Action invoke, bool isEnabled)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("A menu entry needs a label.", nameof(label));
            Name = CustomName;
            Label = label;
            Kind = TiroMenuEntryKind.Command;
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
            _isEnabled = isEnabled;
        }

        private TiroMenuEntry()
        {
            Name = SeparatorName;
            Kind = TiroMenuEntryKind.Separator;
            _isEnabled = true;
        }

        /// <summary>
        /// A dividing line. Each call returns a fresh entry, so a menu may carry several; the
        /// browser layer pools the underlying objects so repeated menus don't accumulate them.
        /// A separator that ends up first or last in the menu still renders, so place them
        /// only between groups.
        /// </summary>
        public static TiroMenuEntry CreateSeparator() => new TiroMenuEntry();

        /// <summary>
        /// The unlocalized identity of the entry, and the only thing worth matching on:
        /// <c>"copy"</c>, <c>"paste"</c>, <c>"inspectElement"</c>, <c>"saveAs"</c>. It is the
        /// English label in lower camel case.
        /// </summary>
        /// <remarks>
        /// Not unique within a menu. The browser gives every spelling suggestion the name
        /// <c>"spellCheck"</c>, and every host entry is <see cref="CustomName"/> — which is why
        /// a menu is a list and not a dictionary. Filter with it; don't key on it.
        /// </remarks>
        public string Name { get; }

        /// <summary>
        /// What the user reads. Localised by the browser for its own entries, and containing an
        /// ampersand before the keyboard-accelerator character (<c>"&amp;Copy"</c>), so it is
        /// unsuitable for matching. Null for a separator.
        /// </summary>
        public string Label { get; }

        /// <summary>How the entry renders.</summary>
        public TiroMenuEntryKind Kind { get; }

        /// <summary>
        /// True when this came from the embedded browser rather than the host. Such an entry may
        /// be reordered or dropped, but not relabelled, disabled, or reconstructed.
        /// </summary>
        public bool IsFromBrowser { get; }

        /// <summary>
        /// Whether the entry is pickable. Settable on host entries only — the embedded browser
        /// requires that its own entries keep the enabled state it computed (it already greys
        /// out what doesn't apply, such as Paste over an empty clipboard). To take a browser
        /// entry out of a menu, leave it out of the returned list instead.
        /// </summary>
        /// <exception cref="InvalidOperationException">The entry came from the browser.</exception>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (IsFromBrowser)
                    throw new InvalidOperationException(
                        "IsEnabled can only be set on a host-supplied entry; the embedded browser owns the " +
                        "enabled state of its own. Omit the entry from the menu to hide it instead.");
                _isEnabled = value;
            }
        }

        /// <summary>The browser layer's own object for this entry; null for a host entry.</summary>
        internal object BrowserHandle { get; }

        /// <summary>The guarded action for a host entry; null for a browser entry or separator.</summary>
        internal Action Invoke { get; }
    }
}
