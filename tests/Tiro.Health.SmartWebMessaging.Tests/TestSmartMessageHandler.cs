using Moq;
using Hl7.Fhir.Model;
using static Hl7.Fhir.Model.QuestionnaireResponse;
using System.Data.SqlTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Fhir.R5;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Collections.Generic;
using Hl7.Fhir.Serialization;
using Tiro.Health.SmartWebMessaging.Message;
using Tiro.Health.SmartWebMessaging.Message.Payload;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations;
using System;
using System.Threading.Tasks;

namespace Tiro.Health.SmartWebMessaging.Tests
{
  [TestClass]
  public sealed class TestSmartMessageHandler
  {
    [TestMethod]
    public void TestScratchpadCreate()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };

      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"}
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);

      // Verify the resource of the event
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QuestionnaireResponse QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.InProgress, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);

      StringAssert.Contains(result, $"\"responseToMessageId\":\"123\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadCreate\",\"status\":\"201 Created\",\"location\":\"QuestionnaireResponse/{QR.Id!}\"");


      // Output the result
      string jsonStringRead = $$"""
            {
                "messageId": "1234",
                "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                "messageType": "scratchpad.read",
                "payload": {
                    "location": "QuestionnaireResponse/{{QR.Id!}}"
                }
            }
            """;
      var resultRead = messageHandler.HandleMessage(jsonStringRead);
      Console.WriteLine($"Message handled: {resultRead}");
      StringAssert.Contains(resultRead, $"\"responseToMessageId\":\"1234\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadRead\",\"resource\":{{\"resourceType\":\"QuestionnaireResponse\",\"id\":\"{QR.Id!}\",\"questionnaire\":\"http://hl7.org/fhir/Questionnaire/gcs\",\"status\":\"in-progress\",\"subject\":{{\"reference\":\"Patient/123\"}},\"encounter\":{{\"reference\":\"Encounter/123\"}}}}}}");
    }

    [TestMethod]
    public void TestScratchpadCreateQR()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };

      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"},
                     "item" : [{
                       "linkId" : "birthDetails",
                       "text" : "Birth details - To be completed by health professional",
                       "item" : [{
                         "linkId" : "group",
                         "item" : [{
                           "linkId" : "nameOfChild",
                           "text" : "Name of child",
                           "answer" : [{
                             "valueString" : "Cathy Jones"
                           }]
                         },
                         {
                           "linkId" : "sex",
                           "text" : "Sex",
                           "answer" : [{
                             "valueCoding" : {
                               "code" : "F"
                             }
                           }]
                         }]
                       },
                       {
                         "linkId" : "neonatalInformation",
                         "text" : "Neonatal Information",
                         "item" : [{
                           "linkId" : "birthWeight",
                           "text" : "Birth weight (kg)",
                           "answer" : [{
                             "valueDecimal" : 3.25
                           }]
                         },
                         {
                           "linkId" : "birthLength",
                           "text" : "Birth length (cm)",
                           "answer" : [{
                             "valueDecimal" : 44.3
                           }]
                         },
                         {
                           "linkId" : "vitaminKgiven",
                           "text" : "Vitamin K given",
                           "answer" : [{
                             "valueCoding" : {
                               "code" : "INJECTION"
                             },
                             "item" : [{
                               "linkId" : "vitaminKgivenDoses",
                               "item" : [{
                                 "linkId" : "vitaminKDose1",
                                 "text" : "1st dose",
                                 "answer" : [{
                                   "valueDateTime" : "1972-11-30"
                                 }]
                               },
                               {
                                 "linkId" : "vitaminKDose2",
                                 "text" : "2nd dose",
                                 "answer" : [{
                                   "valueDateTime" : "1972-12-11"
                                 }]
                               }]
                             }]
                           }]
                         },
                         {
                           "linkId" : "hepBgiven",
                           "text" : "Hep B given y / n",
                           "answer" : [{
                             "valueBoolean" : true,
                             "item" : [{
                               "linkId" : "hepBgivenDate",
                               "text" : "Date given",
                               "answer" : [{
                                 "valueDate" : "1972-12-04"
                               }]
                             }]
                           }]
                         },
                         {
                           "linkId" : "abnormalitiesAtBirth",
                           "text" : "Abnormalities noted at birth",
                           "answer" : [{
                             "valueString" : "Already able to speak Chinese"
                           }]
                         }]
                       }]
                     }]
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);

      // Verify the resource of the event
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QuestionnaireResponse QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.InProgress, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);
      Assert.AreEqual(QR.Item.Count, 1);
      Assert.AreEqual(QR.Item[0].LinkId, "birthDetails");
      Assert.AreEqual(QR.Item[0].Item.Count, 2);
      Assert.AreEqual(QR.Item[0].Item[1].LinkId, "neonatalInformation");
      Assert.AreEqual(QR.Item[0].Item[1].Item.Count, 5);
      Assert.AreEqual(QR.Item[0].Item[1].Item[0].LinkId, "birthWeight");
      var valueDecimal = QR.Item[0].Item[1].Item[0].Answer[0].Value as FhirDecimal;
      Assert.IsNotNull(valueDecimal, "The Answer.Value should be a FhirDecimal");
      Assert.AreEqual(3.25m, valueDecimal.Value);

      // Output the result
      string jsonStringRead = $$"""
            {
                "messageId": "1234",
                "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                "messageType": "scratchpad.read",
                "payload": {
                    "location": "QuestionnaireResponse/{{QR.Id!}}"
                }
            }
            """;
      var resultRead = messageHandler.HandleMessage(jsonStringRead);
      Console.WriteLine($"Message handled: {resultRead}");
      StringAssert.Contains(resultRead, "\"responseToMessageId\":\"1234\"");
      StringAssert.Contains(resultRead, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(resultRead, "\"$type\":\"scratchpadRead\"");
      StringAssert.Contains(resultRead, "\"resourceType\":\"QuestionnaireResponse\"");
      StringAssert.Contains(resultRead, "\"questionnaire\":\"http://hl7.org/fhir/Questionnaire/gcs\"");
      StringAssert.Contains(resultRead, "\"status\":\"in-progress\"");
      StringAssert.Contains(resultRead, "\"reference\":\"Patient/123\"");
      StringAssert.Contains(resultRead, "\"reference\":\"Encounter/123\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"birthDetails\"");
      StringAssert.Contains(resultRead, "\"text\":\"Birth details - To be completed by health professional\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"group\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"nameOfChild\"");
      StringAssert.Contains(resultRead, "\"text\":\"Name of child\"");
      StringAssert.Contains(resultRead, "\"valueString\":\"Cathy Jones\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"sex\"");
      StringAssert.Contains(resultRead, "\"text\":\"Sex\"");
      StringAssert.Contains(resultRead, "\"valueCoding\":{\"code\":\"F\"}");
      StringAssert.Contains(resultRead, "\"linkId\":\"neonatalInformation\"");
      StringAssert.Contains(resultRead, "\"text\":\"Neonatal Information\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"birthWeight\"");
      StringAssert.Contains(resultRead, "\"text\":\"Birth weight (kg)\"");
      StringAssert.Contains(resultRead, "\"valueDecimal\":3.25");
      StringAssert.Contains(resultRead, "\"linkId\":\"birthLength\"");
      StringAssert.Contains(resultRead, "\"text\":\"Birth length (cm)\"");
      StringAssert.Contains(resultRead, "\"valueDecimal\":44.3");
      StringAssert.Contains(resultRead, "\"linkId\":\"vitaminKgiven\"");
      StringAssert.Contains(resultRead, "\"text\":\"Vitamin K given\"");
      StringAssert.Contains(resultRead, "\"valueCoding\":{\"code\":\"INJECTION\"}");
      StringAssert.Contains(resultRead, "\"linkId\":\"vitaminKDose1\"");
      StringAssert.Contains(resultRead, "\"text\":\"1st dose\"");
      StringAssert.Contains(resultRead, "\"valueDateTime\":\"1972-11-30\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"vitaminKDose2\"");
      StringAssert.Contains(resultRead, "\"text\":\"2nd dose\"");
      StringAssert.Contains(resultRead, "\"valueDateTime\":\"1972-12-11\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"hepBgiven\"");
      StringAssert.Contains(resultRead, "\"text\":\"Hep B given y / n\"");
      StringAssert.Contains(resultRead, "\"valueBoolean\":true");
      StringAssert.Contains(resultRead, "\"linkId\":\"hepBgivenDate\"");
      StringAssert.Contains(resultRead, "\"text\":\"Date given\"");
      StringAssert.Contains(resultRead, "\"valueDate\":\"1972-12-04\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"abnormalitiesAtBirth\"");
      StringAssert.Contains(resultRead, "\"text\":\"Abnormalities noted at birth\"");
      StringAssert.Contains(resultRead, "\"valueString\":\"Already able to speak Chinese\"");
    }

    [TestMethod]
    public void TestScratchpadCreateQRParseError()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var serializeOptions = new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
      }.ForFhir(ModelInfo.ModelInspector).UsingMode(DeserializerModes.Recoverable);
      var messageHandler = new SmartMessageHandler(serializeOptions);

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };

      // Create a sample message to test
      // QR with a parse error: "3.25" instead of 3.25 for a decimal
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"},
                     "item" : [{
                       "linkId" : "birthDetails",
                       "text" : "Birth details - To be completed by health professional",
                       "item" : [{
                         "linkId" : "group",
                         "item" : [{
                           "linkId" : "nameOfChild",
                           "text" : "Name of child",
                           "answer" : [{
                             "valueString" : "Cathy Jones"
                           }]
                         },
                         {
                           "linkId" : "sex",
                           "text" : "Sex",
                           "answer" : [{
                             "valueCoding" : {
                               "code" : "F"
                             }
                           }]
                         }]
                       },
                       {
                         "linkId" : "neonatalInformation",
                         "text" : "Neonatal Information",
                         "item" : [{
                           "linkId" : "birthWeight",
                           "text" : "Birth weight (kg)",
                           "answer" : [{
                             "valueDecimal" : "3.25"
                           }]
                         },
                         {
                           "linkId" : "birthLength",
                           "text" : "Birth length (cm)",
                           "answer" : [{
                             "valueDecimal" : 44.3
                           }]
                         },
                         {
                           "linkId" : "vitaminKgiven",
                           "text" : "Vitamin K given",
                           "answer" : [{
                             "valueCoding" : {
                               "code" : "INJECTION"
                             },
                             "item" : [{
                               "linkId" : "vitaminKgivenDoses",
                               "item" : [{
                                 "linkId" : "vitaminKDose1",
                                 "text" : "1st dose",
                                 "answer" : [{
                                   "valueDateTime" : "1972-11-30"
                                 }]
                               },
                               {
                                 "linkId" : "vitaminKDose2",
                                 "text" : "2nd dose",
                                 "answer" : [{
                                   "valueDateTime" : "1972-12-11"
                                 }]
                               }]
                             }]
                           }]
                         },
                         {
                           "linkId" : "hepBgiven",
                           "text" : "Hep B given y / n",
                           "answer" : [{
                             "valueBoolean" : true,
                             "item" : [{
                               "linkId" : "hepBgivenDate",
                               "text" : "Date given",
                               "answer" : [{
                                 "valueDate" : "1972-12-04"
                               }]
                             }]
                           }]
                         },
                         {
                           "linkId" : "abnormalitiesAtBirth",
                           "text" : "Abnormalities noted at birth",
                           "answer" : [{
                             "valueString" : "Already able to speak Chinese"
                           }]
                         }]
                       }]
                     }]
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);

      // Verify the resource of the event
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QuestionnaireResponse QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.InProgress, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);
      Assert.AreEqual(QR.Item.Count, 1);
      Assert.AreEqual(QR.Item[0].LinkId, "birthDetails");
      Assert.AreEqual(QR.Item[0].Item.Count, 2);
      Assert.AreEqual(QR.Item[0].Item[1].LinkId, "neonatalInformation");
      Assert.AreEqual(QR.Item[0].Item[1].Item.Count, 5);
      Assert.AreEqual(QR.Item[0].Item[1].Item[0].LinkId, "birthWeight");
      //var valueDecimal = QR.Item[0].Item[1].Item[0].Answer[0].Value as FhirDecimal;
      //Assert.IsNotNull(valueDecimal, "The Answer.Value should be a FhirDecimal");
      //Assert.AreEqual(3.25m, valueDecimal.Value);

      // Output the result
      string jsonStringRead = $$"""
            {
                "messageId": "1234",
                "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                "messageType": "scratchpad.read",
                "payload": {
                    "location": "QuestionnaireResponse/{{QR.Id!}}"
                }
            }
            """;
      var resultRead = messageHandler.HandleMessage(jsonStringRead);
      Console.WriteLine($"Message handled: {resultRead}");
      StringAssert.Contains(resultRead, "\"responseToMessageId\":\"1234\"");
      StringAssert.Contains(resultRead, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(resultRead, "\"$type\":\"scratchpadRead\"");
      StringAssert.Contains(resultRead, "\"resourceType\":\"QuestionnaireResponse\"");
      StringAssert.Contains(resultRead, "\"questionnaire\":\"http://hl7.org/fhir/Questionnaire/gcs\"");
      StringAssert.Contains(resultRead, "\"status\":\"in-progress\"");
      StringAssert.Contains(resultRead, "\"reference\":\"Patient/123\"");
      StringAssert.Contains(resultRead, "\"reference\":\"Encounter/123\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"birthDetails\"");
      StringAssert.Contains(resultRead, "\"text\":\"Birth details - To be completed by health professional\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"group\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"nameOfChild\"");
      StringAssert.Contains(resultRead, "\"text\":\"Name of child\"");
      StringAssert.Contains(resultRead, "\"valueString\":\"Cathy Jones\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"sex\"");
      StringAssert.Contains(resultRead, "\"text\":\"Sex\"");
      StringAssert.Contains(resultRead, "\"valueCoding\":{\"code\":\"F\"}");
      StringAssert.Contains(resultRead, "\"linkId\":\"neonatalInformation\"");
      StringAssert.Contains(resultRead, "\"text\":\"Neonatal Information\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"birthWeight\"");
      StringAssert.Contains(resultRead, "\"text\":\"Birth weight (kg)\"");
      StringAssert.Contains(resultRead, "\"valueDecimal\":\"3.25\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"birthLength\"");
      StringAssert.Contains(resultRead, "\"text\":\"Birth length (cm)\"");
      StringAssert.Contains(resultRead, "\"valueDecimal\":44.3");
      StringAssert.Contains(resultRead, "\"linkId\":\"vitaminKgiven\"");
      StringAssert.Contains(resultRead, "\"text\":\"Vitamin K given\"");
      StringAssert.Contains(resultRead, "\"valueCoding\":{\"code\":\"INJECTION\"}");
      StringAssert.Contains(resultRead, "\"linkId\":\"vitaminKDose1\"");
      StringAssert.Contains(resultRead, "\"text\":\"1st dose\"");
      StringAssert.Contains(resultRead, "\"valueDateTime\":\"1972-11-30\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"vitaminKDose2\"");
      StringAssert.Contains(resultRead, "\"text\":\"2nd dose\"");
      StringAssert.Contains(resultRead, "\"valueDateTime\":\"1972-12-11\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"hepBgiven\"");
      StringAssert.Contains(resultRead, "\"text\":\"Hep B given y / n\"");
      StringAssert.Contains(resultRead, "\"valueBoolean\":true");
      StringAssert.Contains(resultRead, "\"linkId\":\"hepBgivenDate\"");
      StringAssert.Contains(resultRead, "\"text\":\"Date given\"");
      StringAssert.Contains(resultRead, "\"valueDate\":\"1972-12-04\"");
      StringAssert.Contains(resultRead, "\"linkId\":\"abnormalitiesAtBirth\"");
      StringAssert.Contains(resultRead, "\"text\":\"Abnormalities noted at birth\"");
      StringAssert.Contains(resultRead, "\"valueString\":\"Already able to speak Chinese\"");
    }

    [TestMethod]
    public void TestScratchpadCreateQRParseErrorStrict()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler with strict mode (no UsingMode)
      var serializeOptions = new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
      }.ForFhir(ModelInfo.ModelInspector);
      var messageHandler = new SmartMessageHandler(serializeOptions);

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };

      // Create a sample message to test
      // QR with a parse error: "3.25" instead of 3.25 for a decimal
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"},
                     "item" : [{
                       "linkId" : "birthDetails",
                       "text" : "Birth details - To be completed by health professional",
                       "item" : [{
                         "linkId" : "group",
                         "item" : [{
                           "linkId" : "nameOfChild",
                           "text" : "Name of child",
                           "answer" : [{
                             "valueString" : "Cathy Jones"
                           }]
                         },
                         {
                           "linkId" : "sex",
                           "text" : "Sex",
                           "answer" : [{
                             "valueCoding" : {
                               "code" : "F"
                             }
                           }]
                         }]
                       },
                       {
                         "linkId" : "neonatalInformation",
                         "text" : "Neonatal Information",
                         "item" : [{
                           "linkId" : "birthWeight",
                           "text" : "Birth weight (kg)",
                           "answer" : [{
                             "valueDecimal" : "3.25"
                           }]
                         },
                         {
                           "linkId" : "birthLength",
                           "text" : "Birth length (cm)",
                           "answer" : [{
                             "valueDecimal" : 44.3
                           }]
                         },
                         {
                           "linkId" : "vitaminKgiven",
                           "text" : "Vitamin K given",
                           "answer" : [{
                             "valueCoding" : {
                               "code" : "INJECTION"
                             },
                             "item" : [{
                               "linkId" : "vitaminKgivenDoses",
                               "item" : [{
                                 "linkId" : "vitaminKDose1",
                                 "text" : "1st dose",
                                 "answer" : [{
                                   "valueDateTime" : "1972-11-30"
                                 }]
                               },
                               {
                                 "linkId" : "vitaminKDose2",
                                 "text" : "2nd dose",
                                 "answer" : [{
                                   "valueDateTime" : "1972-12-11"
                                 }]
                               }]
                             }]
                           }]
                         },
                         {
                           "linkId" : "hepBgiven",
                           "text" : "Hep B given y / n",
                           "answer" : [{
                             "valueBoolean" : true,
                             "item" : [{
                               "linkId" : "hepBgivenDate",
                               "text" : "Date given",
                               "answer" : [{
                                 "valueDate" : "1972-12-04"
                               }]
                             }]
                           }]
                         },
                         {
                           "linkId" : "abnormalitiesAtBirth",
                           "text" : "Abnormalities noted at birth",
                           "answer" : [{
                             "valueString" : "Already able to speak Chinese"
                           }]
                         }]
                       }]
                     }]
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");
      StringAssert.Contains(result, "\"errorMessage\":\"One or more errors occurred. (Expecting a Decimal, but found a json string with value '3.25'. At QuestionnaireResponse.item[0].item[1].item[0].answer[0].value, line 1, position 672)\"");
      StringAssert.Contains(result, "\"errorType\":\"DeserializationFailedException\"");

      // Verify that the ResourceChangedEvent was never fired
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Never);
    }

    [TestMethod]
    public void TestScratchpadUpdate()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };


      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"}
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);

      // Verify the resource of the event
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QuestionnaireResponse QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.InProgress, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);

      StringAssert.Contains(result, $"\"responseToMessageId\":\"123\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadCreate\",\"status\":\"201 Created\",\"location\":\"QuestionnaireResponse/{QR.Id!}\"");


      // Output the result
      string jsonStringUpdate = $$"""
            {
                "messageId": "1234",
                "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                "messageType": "scratchpad.update",
                "payload": {
                  "resource": {
                    "id": "{{QR.Id!}}",
                    "resourceType": "QuestionnaireResponse",
                    "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                    "status": "completed",
                    "subject": {"reference": "Patient/123"},
                    "encounter": {"reference": "Encounter/123"}
                  }
                }
            }
            """;
      mockEventHandler.Reset();
      var resultUpdate = messageHandler.HandleMessage(jsonStringUpdate);
      Console.WriteLine($"Message handled: {resultUpdate}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.Completed, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);

      StringAssert.Contains(resultUpdate, $"\"responseToMessageId\":\"1234\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadUpdate\",\"status\":\"200 OK\"");
    }

    [TestMethod]
    public void TestScratchpadDelete()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };


      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"}
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);

      // Verify the resource of the event
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QuestionnaireResponse QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.InProgress, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);

      StringAssert.Contains(result, $"\"responseToMessageId\":\"123\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadCreate\",\"status\":\"201 Created\",\"location\":\"QuestionnaireResponse/{QR.Id!}\"");


      // Output the result
      string jsonStringDelete = $$"""
            {
                "messageId": "1234",
                "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                "messageType": "scratchpad.delete",
                "payload": {
                    "location": "QuestionnaireResponse/{{QR.Id!}}"
                }
            }
            """;
      mockEventHandler.Reset();
      var resultDelete = messageHandler.HandleMessage(jsonStringDelete);
      Console.WriteLine($"Message handled: {resultDelete}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Never);

      StringAssert.Contains(resultDelete, $"\"responseToMessageId\":\"1234\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadDelete\",\"status\":\"200 OK\"");
    }

    [TestMethod]
    public void TestScratchpadRead()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      ResourceChangedEventArgs capturedArgs = null!;

      // Subscribe the mock event handler to the ResourceChangedEvent
      messageHandler.ResourceChanged += (sender, args) =>
      {
        mockEventHandler.Object(sender, args);
        capturedArgs = args!;
      };


      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {
                   "resource": {
                     "resourceType": "QuestionnaireResponse",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/gcs",
                     "status": "in-progress",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/123"}
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Once);

      // Verify the resource of the event
      Assert.IsNotNull(capturedArgs);
      Assert.AreEqual("QuestionnaireResponse", capturedArgs.Resource.TypeName);
      QuestionnaireResponse QR = capturedArgs.Resource as QuestionnaireResponse ?? throw new InvalidOperationException("Resource is not a QuestionnaireResponse");
      Assert.IsNotNull(QR);
      Assert.AreEqual(QuestionnaireResponseStatus.InProgress, QR.Status);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/gcs", QR.Questionnaire);
      Assert.AreEqual("Patient/123", QR.Subject!.Reference);
      Assert.AreEqual("Encounter/123", QR.Encounter!.Reference);
      Assert.IsNotNull(QR.Id);

      StringAssert.Contains(result, $"\"responseToMessageId\":\"123\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadCreate\",\"status\":\"201 Created\",\"location\":\"QuestionnaireResponse/{QR.Id!}\"");


      // Output the result
      string jsonStringRead = $$"""
            {
                "messageId": "1234",
                "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                "messageType": "scratchpad.read",
                "payload": {
                }
            }
            """;
      mockEventHandler.Reset();
      var resultRead = messageHandler.HandleMessage(jsonStringRead);
      Console.WriteLine($"Message handled: {resultRead}");

      // Verify that the ResourceChangedEvent was fired once
      mockEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<ResourceChangedEventArgs>()), Times.Never);

      StringAssert.Contains(resultRead, $"\"responseToMessageId\":\"1234\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"scratchpadRead\",\"scratchpad\":[{{\"resourceType\":\"QuestionnaireResponse\",\"id\":\"{QR.Id!}\",\"questionnaire\":\"http://hl7.org/fhir/Questionnaire/gcs\",\"status\":\"in-progress\",\"subject\":{{\"reference\":\"Patient/123\"}},\"encounter\":{{\"reference\":\"Encounter/123\"");

    }


    [TestMethod]
    public void TestHandshake()
    {
      // Create mocks for the event handlers
      var mockResourceChangedEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();
      var mockHandshakeEventHandler = new Mock<EventHandler<HandshakeReceivedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the handshake event args
      HandshakeReceivedEventArgs capturedHandshakeArgs = null!;

      // Subscribe to the HandshakeReceived event
      messageHandler.HandshakeReceived += (sender, args) =>
      {
        mockHandshakeEventHandler.Object(sender, args);
        capturedHandshakeArgs = args!;
      };

      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "status.handshake",
                 "payload": {}
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify response structure
      StringAssert.Contains(result, $"\"responseToMessageId\":\"123\",\"additionalResponsesExpected\":false,\"payload\":{{\"$type\":\"base\"}}");

      // Verify that the HandshakeReceived event was fired once
      mockHandshakeEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<HandshakeReceivedEventArgs>()), Times.Once);

      // Verify the event args contain the correct data
      Assert.IsNotNull(capturedHandshakeArgs);
      Assert.IsNotNull(capturedHandshakeArgs.Message);
      Assert.IsNotNull(capturedHandshakeArgs.Payload);
      Assert.AreEqual("123", capturedHandshakeArgs.Message.MessageId);
      Assert.AreEqual("bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo", capturedHandshakeArgs.Message.MessagingHandle);
      Assert.AreEqual("status.handshake", capturedHandshakeArgs.Message.MessageType);
    }

    [TestMethod]
    public void TestUnknownMessageType()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "??",
                 "payload": {}
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      StringAssert.Contains(result, "\"responseToMessageId\":\"123\"");
      StringAssert.Contains(result, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(result, "\"payload\":{\"$type\":\"error\",\"errorMessage\":\"Unknown messageType: ??\",\"errorType\":\"UnknownMessageTypeException\"}");
    }

    [TestMethod]
    public void TestParsingFaillure()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Create a sample message to test
      string jsonString = """
                {
                 "MessageId": "123",,
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {}
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      StringAssert.Contains(result, "\"responseToMessageId\":\"123\"");
      StringAssert.Contains(result, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(result, "\"payload\":{\"$type\":\"error\"");
      StringAssert.Contains(result, "\"errorType\":\"JsonException\"}");
    }

    [TestMethod]
    public void TestParsingFaillureNoMessageId()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Create a sample message to test
      string jsonString = """
                {
                 "messagingHandle": "bws8YCbyBtCYi5mWVgUDRqX8xcjiudCo",
                 "messageType": "scratchpad.create",
                 "payload": {}
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      StringAssert.Contains(result, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(result, "\"payload\":{\"$type\":\"error\",\"errorMessage\":\"MessageId is mandatory.\",\"errorType\":\"ValidationException\"}");
      Assert.IsFalse(result.Contains("\"responseToMessageId\""));
    }

    [TestMethod]
    public void TestMissingField()
    {
      // Create a mock for the event handler
      var mockEventHandler = new Mock<EventHandler<ResourceChangedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Create a sample message to test
      string jsonString = """
                {
                 "messageId": "123",
                 "messageType": "scratchpad.create",
                 "payload": {}
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      StringAssert.Contains(result, "\"responseToMessageId\":\"123\"");
      StringAssert.Contains(result, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(result, "\"payload\":{\"$type\":\"error\",\"errorMessage\":\"MessagingHandle is mandatory.\",\"errorType\":\"ValidationException\"}");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendRequestAsyncWithListener()
    {
      var messageHandler = new SmartMessageHandler();
      var mockSender = new Mock<SmartMessageHandler.MessageSender>();
      SmartMessageResponse capturedResponse = null!;
      bool listenerCalled = false;

      // Set up the mock sender to return a response
      mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
               .ReturnsAsync("mock response");

      messageHandler.SendMessage = mockSender.Object;

      // Create a test request
      var request = new SmartMessageRequest(
          "test-123",
          "smart-web-messaging",
          "status.handshake",
          new RequestPayload()
      );

      // Set up response listener
      var responseListener = new Func<SmartMessageResponse, System.Threading.Tasks.Task>(response =>
      {
        capturedResponse = response;
        listenerCalled = true;
        return System.Threading.Tasks.Task.CompletedTask;
      });

      // Send request with listener
      var result = await messageHandler.SendRequestAsync(request, responseListener);

      // Verify the request was sent
      mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
      Assert.AreEqual("mock response", result);

      // Verify listener was registered
      Assert.IsTrue(messageHandler.HasPendingResponseListener("test-123"));

      // Simulate receiving a response message
      var responseMessage = new SmartMessageResponse("response-456", "test-123", false, new ResponsePayload());
      string responseJson = JsonSerializer.Serialize(responseMessage, messageHandler.SerializeOptions);

      // Handle the response message - should trigger the listener
      var handlerResult = messageHandler.HandleMessage(responseJson);

      // Small delay to allow async listener to execute
      await System.Threading.Tasks.Task.Delay(10);

      // Verify the listener was called
      Assert.IsTrue(listenerCalled);
      Assert.IsNotNull(capturedResponse);
      Assert.AreEqual("test-123", capturedResponse.ResponseToMessageId!);
      Assert.AreEqual("response-456", capturedResponse.MessageId!);

      // Verify listener was removed since AdditionalResponsesExpected = false
      Assert.IsFalse(messageHandler.HasPendingResponseListener("test-123"));

      // Response messages should not generate replies
      Assert.IsNull(handlerResult);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendRequestAsyncWithoutListener()
    {
      var messageHandler = new SmartMessageHandler();
      var mockSender = new Mock<SmartMessageHandler.MessageSender>();

      // Set up the mock sender
      mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
               .ReturnsAsync("mock response");

      messageHandler.SendMessage = mockSender.Object;

      // Send request without listener
      var result = await messageHandler.SendRequestAsync("status.handshake", new RequestPayload());

      // Verify the request was sent
      mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
      Assert.AreEqual("mock response", result);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestMultipleResponsesExpected()
    {
      var messageHandler = new SmartMessageHandler();
      var responses = new List<SmartMessageResponse>();
      bool listenerStillRegistered = false;

      // Create a test request
      var request = new SmartMessageRequest(
          "multi-test-123",
          "smart-web-messaging",
          "scratchpad.read",
          new ScratchpadRead(null)
      );

      // Set up response listener
      var responseListener = new Func<SmartMessageResponse, System.Threading.Tasks.Task>(response =>
      {
        responses.Add(response);
        listenerStillRegistered = messageHandler.HasPendingResponseListener("multi-test-123");
        return System.Threading.Tasks.Task.CompletedTask;
      });

      // Register the listener manually
      messageHandler.RegisterResponseListener("multi-test-123", responseListener);

      // Simulate first response with additional responses expected
      var response1 = new SmartMessageResponse("resp-1", "multi-test-123", true, new ResponsePayload());
      string response1Json = JsonSerializer.Serialize(response1, messageHandler.SerializeOptions);
      messageHandler.HandleMessage(response1Json);

      await System.Threading.Tasks.Task.Delay(10);

      // Verify first response was processed and listener still registered
      Assert.AreEqual(1, responses.Count);
      Assert.IsTrue(listenerStillRegistered);
      Assert.IsTrue(messageHandler.HasPendingResponseListener("multi-test-123"));

      // Simulate second response with no additional responses expected
      var response2 = new SmartMessageResponse("resp-2", "multi-test-123", false, new ResponsePayload());
      string response2Json = JsonSerializer.Serialize(response2, messageHandler.SerializeOptions);
      messageHandler.HandleMessage(response2Json);

      await System.Threading.Tasks.Task.Delay(10);

      // Verify both responses were processed and listener was removed
      Assert.AreEqual(2, responses.Count);
      Assert.IsFalse(messageHandler.HasPendingResponseListener("multi-test-123"));
    }

    [TestMethod]
    public void TestListenerManagement()
    {
      var messageHandler = new SmartMessageHandler();
      var testListener = new Func<SmartMessageResponse, System.Threading.Tasks.Task>(response => System.Threading.Tasks.Task.CompletedTask);

      // Test registration
      messageHandler.RegisterResponseListener("test-id", testListener);
      Assert.IsTrue(messageHandler.HasPendingResponseListener("test-id"));

      // Test unregistration
      messageHandler.UnregisterResponseListener("test-id");
      Assert.IsFalse(messageHandler.HasPendingResponseListener("test-id"));

      // Test clear all
      messageHandler.RegisterResponseListener("id1", testListener);
      messageHandler.RegisterResponseListener("id2", testListener);
      Assert.IsTrue(messageHandler.HasPendingResponseListener("id1"));
      Assert.IsTrue(messageHandler.HasPendingResponseListener("id2"));

      messageHandler.ClearAllResponseListeners();
      Assert.IsFalse(messageHandler.HasPendingResponseListener("id1"));
      Assert.IsFalse(messageHandler.HasPendingResponseListener("id2"));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendRequestWithoutSenderThrowsException()
    {
      var messageHandler = new SmartMessageHandler();
      // Don't set SendMessage delegate

      var request = new SmartMessageRequest(
          "test-123",
          "smart-web-messaging",
          "status.handshake",
          new RequestPayload()
      );

      await Assert.ThrowsExceptionAsync<InvalidOperationException>(
          async () => await messageHandler.SendRequestAsync(request)
      );
    }

    [TestMethod]
    public void TestResponseMessageDoesNotTriggerRequestHandling()
    {
      var messageHandler = new SmartMessageHandler();

      // Create a response message (has ResponseToMessageId)
      var responseMessage = new SmartMessageResponse("resp-123", "original-456", false, new ResponsePayload());
      string responseJson = JsonSerializer.Serialize(responseMessage, messageHandler.SerializeOptions);

      // Handle the response message
      var result = messageHandler.HandleMessage(responseJson);

      // Response messages should not generate replies
      Assert.IsNull(result);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendMessageAsync()
    {
      var messageHandler = new SmartMessageHandler();
      var mockSender = new Mock<SmartMessageHandler.MessageSender>();
      string sentMessage = null!;

      // Set up the mock sender to capture the sent message
      mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
               .Callback<string>(msg => sentMessage = msg)
               .ReturnsAsync("mock response");

      messageHandler.SendMessage = mockSender.Object;

      // Send a generic message
      var result = await messageHandler.SendMessageAsync("test.message", new RequestPayload());

      // Verify the request was sent
      mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
      Assert.AreEqual("mock response", result);

      // Verify the message structure
      Assert.IsNotNull(sentMessage);
      Assert.IsTrue(sentMessage.Contains("\"messageType\":\"test.message\""));
      Assert.IsTrue(sentMessage.Contains("\"messagingHandle\":\"smart-web-messaging\""));
      Assert.IsTrue(sentMessage.Contains("\"payload\":{"));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendFormRequestSubmitAsync()
    {
      var messageHandler = new SmartMessageHandler();
      var mockSender = new Mock<SmartMessageHandler.MessageSender>();
      string sentMessage = null!;

      // Set up the mock sender
      mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
               .Callback<string>(msg => sentMessage = msg)
               .ReturnsAsync("submit response");

      messageHandler.SendMessage = mockSender.Object;

      // Send form request submit with response handler
      var result = await messageHandler.SendFormRequestSubmitAsync(response =>
      {
        // Response handler callback
        return System.Threading.Tasks.Task.CompletedTask;
      });

      // Verify the request was sent
      mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
      Assert.AreEqual("submit response", result);

      // Verify the message structure
      Assert.IsNotNull(sentMessage);
      Assert.IsTrue(sentMessage.Contains("\"messageType\":\"ui.form.requestSubmit\""));
      Assert.IsTrue(sentMessage.Contains("\"messagingHandle\":\"smart-web-messaging\""));

      // Verify response listener was registered (we can't easily test the callback without more setup)
      // The existence of the response handler in the call indicates it was registered
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendFormPersistAsync()
    {
      var messageHandler = new SmartMessageHandler();
      var mockSender = new Mock<SmartMessageHandler.MessageSender>();
      string sentMessage = null!;

      // Set up the mock sender
      mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
               .Callback<string>(msg => sentMessage = msg)
               .ReturnsAsync("persist response");

      messageHandler.SendMessage = mockSender.Object;

      // Send form persist without response handler
      var result = await messageHandler.SendFormPersistAsync();

      // Verify the request was sent
      mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
      Assert.AreEqual("persist response", result);

      // Verify the message structure
      Assert.IsNotNull(sentMessage);
      Assert.IsTrue(sentMessage.Contains("\"messageType\":\"ui.form.persist\""));
      Assert.IsTrue(sentMessage.Contains("\"messagingHandle\":\"smart-web-messaging\""));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendSdcConfigureAsync()
    {
        var messageHandler = new SmartMessageHandler();
        var mockSender = new Mock<SmartMessageHandler.MessageSender>();
        string sentMessage = null!;

        // Set up the mock sender
        mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
                 .Callback<string>(msg => sentMessage = msg)
                 .ReturnsAsync("configure response");

        messageHandler.SendMessage = mockSender.Object;

        // ARRANGE setup variables
        var configObject = new { timeout = 5000, maxRetries = 3 };

        // ACT
        var result = await messageHandler.SendSdcConfigureAsync(
            terminologyServer: "http://term.example.com/fhir",
            dataServer: "http://data.example.com/fhir",
            configuration: configObject
        );

        // ASSERT
        mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
        Assert.AreEqual("configure response", result);

        // Verify the message structure
        Assert.IsNotNull(sentMessage);
        Assert.IsTrue(sentMessage.Contains("\"messageType\":\"sdc.configure\""));
        Assert.IsTrue(sentMessage.Contains("\"terminologyServer\":\"http://term.example.com/fhir\""));
        Assert.IsTrue(sentMessage.Contains("\"dataServer\":\"http://data.example.com/fhir\""));
        // Verify configuration object serialization
        Assert.IsTrue(sentMessage.Contains("\"configuration\":{\"timeout\":5000,\"maxRetries\":3}"));
    }

    // Test for sdc.configureContext using FHIR resources with new simplified approach
    [TestMethod]
    public async System.Threading.Tasks.Task TestSendSdcConfigureContextAsync_FhirResources()
    {
        var messageHandler = new SmartMessageHandler();
        var mockSender = new Mock<SmartMessageHandler.MessageSender>();
        string sentMessage = null!;

        // Set up the mock sender
        mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
                 .Callback<string>(msg => sentMessage = msg)
                 .ReturnsAsync("configureContext response");

        messageHandler.SendMessage = mockSender.Object;

        // ARRANGE setup FHIR resources
        var patient = new Patient { Id = "P100", Gender = AdministrativeGender.Male };
        var encounter = new Encounter { Id = "E200", Status = EncounterStatus.Cancelled };
        var author = new Practitioner { Id = "PR789" };

        // ACT
        var result = await messageHandler.SendSdcConfigureContextAsync(
            patient: patient,
            encounter: encounter,
            author: author
        );

        // ASSERT
        mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
        Assert.AreEqual("configureContext response", result);

        // Verify the message structure
        Assert.IsNotNull(sentMessage);
        Assert.IsTrue(sentMessage.Contains("\"messageType\":\"sdc.configureContext\""));
        
        // Verify FHIR resources are included in launchContext with proper structure
        Assert.IsTrue(sentMessage.Contains("\"name\":\"patient\""));
        Assert.IsTrue(sentMessage.Contains("\"name\":\"encounter\""));
        Assert.IsTrue(sentMessage.Contains("\"name\":\"user\""));
        
        // Verify the actual resources are in contentResource fields
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Patient\",\"id\":\"P100\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Encounter\",\"id\":\"E200\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Practitioner\",\"id\":\"PR789\""));
    }

    // New test for sdc.configureContext using generic references and context objects
    [TestMethod]
    public async System.Threading.Tasks.Task TestSendSdcConfigureContextAsync_GenericContext()
    {
        var messageHandler = new SmartMessageHandler();
        var mockSender = new Mock<SmartMessageHandler.MessageSender>();
        string sentMessage = null!;

        // Set up the mock sender
        mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
                 .Callback<string>(msg => sentMessage = msg)
                 .ReturnsAsync("configureContext response");

        messageHandler.SendMessage = mockSender.Object;

        // ARRANGE setup variables
        var subjectRef = new ResourceReference("Organization/ORG1");
        var customContext = new List<LaunchContext<Resource>>
        {
            new LaunchContext<Resource>("purpose", contentResource: new Basic { Id = "launch" }),
            new LaunchContext<Resource>("sessionId", contentResource: new Basic { Id = "sessionId-xyz" })
        };

        // ACT
        var result = await messageHandler.SendSdcConfigureContextAsync(
            subject: subjectRef,
            launchContext: customContext
        );

        // ASSERT
        mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
        Assert.AreEqual("configureContext response", result);

        // Verify the message structure
        Assert.IsNotNull(sentMessage);
        Assert.IsTrue(sentMessage.Contains("\"messageType\":\"sdc.configureContext\""));
        Assert.IsTrue(sentMessage.Contains("\"subject\":{\"reference\":\"Organization/ORG1\"}"));
        // Verify custom objects in the launchContext with proper name structure
        Assert.IsTrue(sentMessage.Contains("\"name\":\"purpose\""));
        Assert.IsTrue(sentMessage.Contains("\"name\":\"sessionId\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Basic\",\"id\":\"launch\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Basic\",\"id\":\"sessionId-xyz\""));
    }

    // New test for sdc.displayQuestionnaire
    [TestMethod]
    public async System.Threading.Tasks.Task TestSendSdcDisplayQuestionnaireAsync_AllParameters()
    {
        var messageHandler = new SmartMessageHandler();
        var mockSender = new Mock<SmartMessageHandler.MessageSender>();
        string sentMessage = null!;

        // Set up the mock sender
        mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
                 .Callback<string>(msg => sentMessage = msg)
                 .ReturnsAsync("displayQuestionnaire response");

        messageHandler.SendMessage = mockSender.Object;

        // ARRANGE setup variables (Assuming Hl7.Fhir.Model types are available)
        var q = new Questionnaire { Id = "SurveyA" };
        var qr = new QuestionnaireResponse { Status = QuestionnaireResponseStatus.Completed, Id = "QRA" };
        var author = new Practitioner { Id = "PR1" };

        // ACT
        var result = await messageHandler.SendSdcDisplayQuestionnaireAsync(
            questionnaire: q,
            questionnaireResponse: qr,
            author: author
        );

        // ASSERT
        mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
        Assert.AreEqual("displayQuestionnaire response", result);

        // Verify the message structure
        Assert.IsNotNull(sentMessage);
        Assert.IsTrue(sentMessage.Contains("\"messageType\":\"sdc.displayQuestionnaire\""));
        // Verify Questionnaire/QuestionnaireResponse are present
        Assert.IsTrue(sentMessage.Contains("\"resourceType\":\"Questionnaire\",\"id\":\"SurveyA\""));
        Assert.IsTrue(sentMessage.Contains("\"resourceType\":\"QuestionnaireResponse\",\"id\":\"QRA\",\"status\":\"completed\""));
        // Verify LaunchContext content with proper structure
        Assert.IsTrue(sentMessage.Contains("\"name\":\"user\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Practitioner\",\"id\":\"PR1\""));
    }
    
    [TestMethod]
    public async System.Threading.Tasks.Task TestSendMessageWithoutSenderDelegate()
    {
      var messageHandler = new SmartMessageHandler();
      // Don't set SendMessage delegate

      // Should throw exception when trying to send
      await Assert.ThrowsExceptionAsync<InvalidOperationException>(
          async () => await messageHandler.SendMessageAsync("test.message")
      );
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendMessageWithPayload()
    {
      var messageHandler = new SmartMessageHandler();
      var mockSender = new Mock<SmartMessageHandler.MessageSender>();
      string sentMessage = null!;

      // Set up the mock sender
      mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
               .Callback<string>(msg => sentMessage = msg)
               .ReturnsAsync("response");

      messageHandler.SendMessage = mockSender.Object;

      // Send message with custom payload
      var customPayload = new RequestPayload();
      var result = await messageHandler.SendMessageAsync("custom.type", customPayload);

      // Verify the request was sent
      mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
      Assert.AreEqual("response", result);

      // Verify the message structure
      Assert.IsNotNull(sentMessage);
      Assert.IsTrue(sentMessage.Contains("\"messageType\":\"custom.type\""));
    }

    [TestMethod]
    public void TestFormSubmit()
    {
      // Create a mock for the FormSubmitted event handler
      var mockFormSubmittedEventHandler = new Mock<EventHandler<FormSubmittedEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      FormSubmittedEventArgs capturedFormSubmittedArgs = null!;

      // Subscribe the mock event handler to the FormSubmitted event
      messageHandler.FormSubmitted += (sender, args) =>
      {
        mockFormSubmittedEventHandler.Object(sender, args);
        capturedFormSubmittedArgs = args!;
      };

      // Create test OperationOutcome with validation errors
      var operationOutcome = new OperationOutcome();
      operationOutcome.Issue.Add(new OperationOutcome.IssueComponent
      {
        Severity = OperationOutcome.IssueSeverity.Error,
        Code = OperationOutcome.IssueType.Required,
        Details = new CodeableConcept { Text = "Patient name is required" },
        Location = new[] { "QuestionnaireResponse.item[0].answer" }
      });
      operationOutcome.Issue.Add(new OperationOutcome.IssueComponent
      {
        Severity = OperationOutcome.IssueSeverity.Warning,
        Code = OperationOutcome.IssueType.Value,
        Details = new CodeableConcept { Text = "Birth weight seems unusually high" },
        Location = new[] { "QuestionnaireResponse.item[1].answer[0].value" }
      });

      // Create test QuestionnaireResponse
      var questionnaireResponse = new QuestionnaireResponse
      {
        Id = "test-qr-123",
        Questionnaire = "http://hl7.org/fhir/Questionnaire/patient-form",
        Status = QuestionnaireResponseStatus.Completed,
        Subject = new ResourceReference("Patient/123"),
        Encounter = new ResourceReference("Encounter/456")
      };
      questionnaireResponse.Item.Add(new QuestionnaireResponse.ItemComponent
      {
        LinkId = "patient-name",
        Text = "Patient Name",
        Answer = new List<QuestionnaireResponse.AnswerComponent>
        {
          new QuestionnaireResponse.AnswerComponent { Value = new FhirString("John Doe") }
        }
      });

      // Create form.submit message JSON
      string jsonString = """
                {
                 "messageId": "form-submit-123",
                 "messagingHandle": "smart-web-messaging",
                 "messageType": "form.submitted",
                 "payload": {
                   "outcome": {
                     "resourceType": "OperationOutcome",
                     "issue": [
                       {
                         "severity": "error",
                         "code": "required",
                         "details": {
                           "text": "Patient name is required"
                         },
                         "location": ["QuestionnaireResponse.item[0].answer"]
                       },
                       {
                         "severity": "warning", 
                         "code": "value",
                         "details": {
                           "text": "Birth weight seems unusually high"
                         },
                         "location": ["QuestionnaireResponse.item[1].answer[0].value"]
                       }
                     ]
                   },
                   "response": {
                     "resourceType": "QuestionnaireResponse",
                     "id": "test-qr-123",
                     "questionnaire": "http://hl7.org/fhir/Questionnaire/patient-form",
                     "status": "completed",
                     "subject": {"reference": "Patient/123"},
                     "encounter": {"reference": "Encounter/456"},
                     "item": [{
                       "linkId": "patient-name",
                       "text": "Patient Name", 
                       "answer": [{
                         "valueString": "John Doe"
                       }]
                     }]
                   }
                 }
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify response structure
      Assert.IsNotNull(result);
      StringAssert.Contains(result, "\"responseToMessageId\":\"form-submit-123\"");
      StringAssert.Contains(result, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(result, "\"$type\":\"base\"");

      // Verify that the FormSubmitted event was fired once
      mockFormSubmittedEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<FormSubmittedEventArgs>()), Times.Once);

      // Verify the event args contain the correct data
      Assert.IsNotNull(capturedFormSubmittedArgs);
      Assert.IsNotNull(capturedFormSubmittedArgs.Response);
      Assert.IsNotNull(capturedFormSubmittedArgs.Outcome);
      
      // Verify QuestionnaireResponse details
      Assert.AreEqual("test-qr-123", capturedFormSubmittedArgs.Response.Id);
      Assert.AreEqual("http://hl7.org/fhir/Questionnaire/patient-form", capturedFormSubmittedArgs.Response.Questionnaire);
      Assert.AreEqual(QuestionnaireResponseStatus.Completed, capturedFormSubmittedArgs.Response.Status);
      Assert.AreEqual("Patient/123", capturedFormSubmittedArgs.Response.Subject!.Reference);
      Assert.AreEqual("Encounter/456", capturedFormSubmittedArgs.Response.Encounter!.Reference);
      Assert.AreEqual(1, capturedFormSubmittedArgs.Response.Item.Count);
      Assert.AreEqual("patient-name", capturedFormSubmittedArgs.Response.Item[0].LinkId);

      // Verify OperationOutcome details  
      Assert.AreEqual(2, capturedFormSubmittedArgs.Outcome.Issue.Count);
      Assert.AreEqual(OperationOutcome.IssueSeverity.Error, capturedFormSubmittedArgs.Outcome.Issue[0].Severity);
      Assert.AreEqual(OperationOutcome.IssueType.Required, capturedFormSubmittedArgs.Outcome.Issue[0].Code);
      Assert.AreEqual("Patient name is required", capturedFormSubmittedArgs.Outcome.Issue[0].Details!.Text);
      Assert.AreEqual(OperationOutcome.IssueSeverity.Warning, capturedFormSubmittedArgs.Outcome.Issue[1].Severity);
      Assert.AreEqual(OperationOutcome.IssueType.Value, capturedFormSubmittedArgs.Outcome.Issue[1].Code);
      Assert.AreEqual("Birth weight seems unusually high", capturedFormSubmittedArgs.Outcome.Issue[1].Details!.Text);
    }

    [TestMethod]
    public void TestUiDone()
    {
      // Create a mock for the event handler
      var mockCloseApplicationEventHandler = new Mock<EventHandler<CloseApplicationEventArgs>>();

      // Create an instance of SmartMessageHandler
      var messageHandler = new SmartMessageHandler();

      // Variable to capture the event args
      CloseApplicationEventArgs capturedCloseApplicationArgs = null!;

      // Subscribe the mock event handler to the CloseApplication event
      messageHandler.CloseApplication += (sender, args) =>
      {
        mockCloseApplicationEventHandler.Object(sender, args);
        capturedCloseApplicationArgs = args!;
      };

      // Create ui.done message JSON
      string jsonString = """
                {
                 "messageId": "ui-done-123",
                 "messagingHandle": "smart-web-messaging", 
                 "messageType": "ui.done",
                 "payload": {}
                }
             """;

      // Handle the message
      var result = messageHandler.HandleMessage(jsonString);
      Console.WriteLine($"Message handled: {result}");

      // Verify response structure
      Assert.IsNotNull(result);
      StringAssert.Contains(result, "\"responseToMessageId\":\"ui-done-123\"");
      StringAssert.Contains(result, "\"additionalResponsesExpected\":false");
      StringAssert.Contains(result, "\"$type\":\"base\"");

      // Verify that the CloseApplication event was fired once
      mockCloseApplicationEventHandler.Verify(m => m(It.IsAny<object>(), It.IsAny<CloseApplicationEventArgs>()), Times.Once);

      // Verify the event args were created
      Assert.IsNotNull(capturedCloseApplicationArgs);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TestSendSdcDisplayQuestionnaireAsync_WithCanonicalUrlAndFhirResources()
    {
        var messageHandler = new SmartMessageHandler();
        var mockSender = new Mock<SmartMessageHandler.MessageSender>();
        string sentMessage = null!;

        // Set up the mock sender
        mockSender.Setup(s => s.Invoke(It.IsAny<string>()))
                 .Callback<string>(msg => sentMessage = msg)
                 .ReturnsAsync("displayQuestionnaire response");

        messageHandler.SendMessage = mockSender.Object;

        // ARRANGE setup FHIR resources
        var patient = new Patient { Id = "P123", Gender = AdministrativeGender.Male };
        var encounter = new Encounter { Id = "E456", Status = EncounterStatus.InProgress };
        var author = new Practitioner { Id = "PR789" };
        var questionnaireResponse = new QuestionnaireResponse 
        { 
            Id = "QR123", 
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress 
        };

        // ACT - Send with canonical URL and FHIR resources
        var result = await messageHandler.SendSdcDisplayQuestionnaireAsync(
            questionnaireCanonicalUrl: "http://hl7.org/fhir/Questionnaire/patient-intake",
            questionnaireResponse: questionnaireResponse,
            patient: patient,
            encounter: encounter,
            author: author
        );

        // ASSERT
        mockSender.Verify(s => s.Invoke(It.IsAny<string>()), Times.Once);
        Assert.AreEqual("displayQuestionnaire response", result);

        // Verify the message structure
        Assert.IsNotNull(sentMessage);
        Assert.IsTrue(sentMessage.Contains("\"messageType\":\"sdc.displayQuestionnaire\""));
        Assert.IsTrue(sentMessage.Contains("\"questionnaire\":\"http://hl7.org/fhir/Questionnaire/patient-intake\""));
        
        // Verify FHIR resources are included in launchContext with proper structure
        Assert.IsTrue(sentMessage.Contains("\"name\":\"patient\""));
        Assert.IsTrue(sentMessage.Contains("\"name\":\"encounter\""));
        Assert.IsTrue(sentMessage.Contains("\"name\":\"user\""));
        
        // Verify the actual resources are in contentResource fields
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Patient\",\"id\":\"P123\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Encounter\",\"id\":\"E456\""));
        Assert.IsTrue(sentMessage.Contains("\"contentResource\":{\"resourceType\":\"Practitioner\",\"id\":\"PR789\""));
    }

  }
}
