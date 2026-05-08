Imports System.Text
Imports Hl7.Fhir.Model

Public Class QuestionnaireResponseHelper

    Private Const NarrativeAlternativeFormatUrl As String =
        "http://fhir.tiro.health/StructureDefinition/narrative-alternative-format"

    ''' <summary>
    ''' Returns the plain-text narrative from the Tiro narrative-alternative-format
    ''' extension on QuestionnaireResponse.text, or an empty string if absent.
    ''' </summary>
    Public Shared Function GetPlainTextNarrative(qResponse As QuestionnaireResponse) As String
        Return GetNarrativeFromExtension(qResponse, "text/plain")
    End Function

    ''' <summary>
    ''' Returns the RTF narrative from the Tiro narrative-alternative-format
    ''' extension on QuestionnaireResponse.text, or an empty string if absent.
    ''' </summary>
    Public Shared Function GetRtfNarrative(qResponse As QuestionnaireResponse) As String
        Return GetNarrativeFromExtension(qResponse, "text/rtf")
    End Function

    Private Shared Function GetNarrativeFromExtension(qResponse As QuestionnaireResponse, contentType As String) As String
        If qResponse Is Nothing OrElse qResponse.Text Is Nothing OrElse qResponse.Text.Extension Is Nothing Then
            Return String.Empty
        End If

        Dim attachmentExtension = qResponse.Text.Extension _
            .OfType(Of Extension)() _
            .FirstOrDefault(Function(ext) _
                ext.Url = NarrativeAlternativeFormatUrl AndAlso
                TypeOf ext.Value Is Attachment AndAlso
                DirectCast(ext.Value, Attachment).ContentType = contentType)

        If attachmentExtension Is Nothing Then Return String.Empty

        Dim attachment = DirectCast(attachmentExtension.Value, Attachment)
        If attachment.Data Is Nothing Then Return String.Empty

        Return Encoding.UTF8.GetString(attachment.Data)
    End Function

End Class
