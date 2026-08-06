using System;

namespace Tiro.Health.SmartWebMessaging.Events
{
    /// <summary>
    /// Triggered when a ui.form.dirtyChanged message is received.
    /// </summary>
    public class FormDirtyChangedEventArgs : EventArgs
    {
        public bool IsDirty { get; }

        public FormDirtyChangedEventArgs(bool isDirty)
        {
            IsDirty = isDirty;
        }
    }
}
