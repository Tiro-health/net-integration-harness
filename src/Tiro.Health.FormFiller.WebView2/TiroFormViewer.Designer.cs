namespace Tiro.Health.FormFiller.WebView2
{
    partial class TiroFormViewer<TResource, TQR, TOO>
    {
        private System.ComponentModel.IContainer components = null;

        [System.Diagnostics.DebuggerNonUserCode]
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    // 1. Unhook browser event
                    if (_browser != null)
                    {
                        _browser.MessageReceived -= OnBrowserMessageReceived;
                    }

                    // 2. Unhook Message Handler events (CRITICAL for memory management)
                    if (_smartWebMessageHandler != null)
                    {
                        _smartWebMessageHandler.FormSubmitted -= OnFormSubmitted;
                        _smartWebMessageHandler.HandshakeReceived -= OnHandshakeReceived;
                        _smartWebMessageHandler.CloseApplication -= OnCloseApplication;
                    }

                    // 3. Cleanup the Sentry transaction
                    FinishSentryTransaction();

                    // 4. Dispose browser adapter (non-Control state); the Control
                    //    itself is disposed via the Controls-collection ownership chain.
                    if (_browser != null)
                    {
                        try { _browser.Dispose(); } catch { /* best-effort */ }
                    }

                    // 5. Dispose child components
                    if (components != null)
                        components.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void InitializeComponent()
        {
            this.SuspendLayout();
            //
            // TiroFormViewer
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Name = "TiroFormViewer";
            this.Size = new System.Drawing.Size(800, 450);
            this.ResumeLayout(false);
        }
    }
}
