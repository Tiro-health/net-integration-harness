namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    /// <summary>
    /// Payload for the inbound <c>ui.form.dirtyChanged</c> notification.
    /// </summary>
    public class FormDirtyChange : RequestPayload
    {
        public bool IsDirty { get; set; }

        // Settable properties + parameterless ctor: matches every other inbound DTO
        // in the protocol (see FormSubmit) and lets System.Text.Json deserialize via
        // the standard property-setter path.
        public FormDirtyChange()
        {
        }

        public FormDirtyChange(bool isDirty)
        {
            IsDirty = isDirty;
        }
    }
}
