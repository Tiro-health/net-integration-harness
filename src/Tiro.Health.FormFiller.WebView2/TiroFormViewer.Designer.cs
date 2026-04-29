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
                    // 1. Transition to Disposed and cancel lifetime token FIRST so any in-flight async
                    //    operations observing the token fail fast with OperationCanceledException
                    //    before we tear down the browser/handler they depend on.
                    MarkDisposed();
                    try { _lifetimeCts.Cancel(); } catch { /* best-effort */ }

                    // 2. Unhook browser event
                    if (_browser != null)
                    {
                        _browser.MessageReceived -= OnBrowserMessageReceived;
                    }

                    // 3. Unhook Message Handler events (CRITICAL for memory management)
                    if (_smartWebMessageHandler != null)
                    {
                        _smartWebMessageHandler.FormSubmitted -= OnFormSubmitted;
                        _smartWebMessageHandler.HandshakeReceived -= OnHandshakeReceived;
                        _smartWebMessageHandler.CloseApplication -= OnCloseApplication;
                    }

                    // 4. Close the telemetry session (final breadcrumb + flush pending events)
                    EndTelemetrySession();

                    // 5. Dispose browser adapter (non-Control state); the Control
                    //    itself is disposed via the Controls-collection ownership chain.
                    if (_browser != null)
                    {
                        try { _browser.Dispose(); } catch { /* best-effort */ }
                    }

                    // 6. Dispose telemetry sink only if we created it (DI-supplied sinks
                    //    are owned by the caller).
                    if (_ownsTelemetrySink && _telemetry != null)
                    {
                        try { _telemetry.Dispose(); } catch { /* best-effort */ }
                    }

                    // 7. Dispose child components
                    if (components != null)
                        components.Dispose();

                    try { _lifetimeCts.Dispose(); } catch { /* best-effort */ }
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
