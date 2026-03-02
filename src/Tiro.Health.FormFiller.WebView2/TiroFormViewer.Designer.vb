<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class TiroFormViewer
    Inherits System.Windows.Forms.UserControl

    'UserControl1 overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing Then
                ' 1. Unhook WebView2 events
                If WebView2Host IsNot Nothing AndAlso WebView2Host.CoreWebView2 IsNot Nothing Then
                    RemoveHandler WebView2Host.CoreWebView2.WebMessageReceived, AddressOf SMARTWebMessageReceived
                    RemoveHandler WebView2Host.CoreWebView2.PermissionRequested, AddressOf OnPermissionRequested
                End If

                ' 2. Unhook Message Handler events (CRITICAL for memory management)
                If smartWebMessageHandler IsNot Nothing Then
                    RemoveHandler smartWebMessageHandler.FormSubmitted, AddressOf MyFormSubmitted
                    RemoveHandler smartWebMessageHandler.HandshakeReceived, AddressOf OnHandshakeReceived
                End If

                ' 3. Cleanup the Sentry transaction 
                FinishSentryTransaction()

                ' 4. Dispose child components
                If components IsNot Nothing Then
                    components.Dispose()
                End If
            End If
        Finally
            ' Always call the base dispose
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.WebView2Host = New Microsoft.Web.WebView2.WinForms.WebView2()
        CType(Me.WebView2Host, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'WebView2Host
        '
        Me.WebView2Host.AllowExternalDrop = True
        Me.WebView2Host.CreationProperties = Nothing
        Me.WebView2Host.DefaultBackgroundColor = System.Drawing.Color.White
        Me.WebView2Host.Dock = System.Windows.Forms.DockStyle.Fill
        Me.WebView2Host.Location = New System.Drawing.Point(0, 0)
        Me.WebView2Host.Name = "WebView2Host"
        Me.WebView2Host.Size = New System.Drawing.Size(800, 450)
        Me.WebView2Host.TabIndex = 0
        Me.WebView2Host.ZoomFactor = 1.0R
        '
        'TiroFormViewer
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.Controls.Add(Me.WebView2Host)
        Me.Name = "TiroFormViewer"
        Me.Size = New System.Drawing.Size(800, 450)
        CType(Me.WebView2Host, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)

    End Sub

    Friend WithEvents WebView2Host As Microsoft.Web.WebView2.WinForms.WebView2
End Class
