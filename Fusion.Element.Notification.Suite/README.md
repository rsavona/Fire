# Notification Suite Element

The Notification Suite provides elements for sending alerts and notifications via **Email** and **SMS**. These elements are integrated into the Fortna Fusion message bus architecture.

## Overview

- **EmailElement**: Sends emails using SMTP.
- **SmsElement**: Sends SMS messages via a REST API (e.g., Twilio).
- **NotificationElementManager**: Orchestrates the notification elements.

## Configuration

To use the Notification Suite, add a element to your `.fusion` or `appsettings.json` configuration using the `NotificationElementManager`.

### Email Configuration Example

| Property | Description | Required |
| :--- | :--- | :--- |
| `Type` | Must be `Email` | Yes |
| `SmtpHost` | The SMTP server address | Yes |
| `SmtpPort` | The SMTP server port (default: 587) | No |
| `FromAddress` | The sender email address | Yes |
| `Username` | SMTP authentication username | No |
| `Password` | SMTP authentication password | No |
| `EnableSsl` | Whether to use SSL/TLS (default: true) | No |
| `DefaultRecipient` | The default email address to send to | No |
| `DefaultSubject` | The default subject line | No |

### SMS Configuration Example

| Property | Description | Required |
| :--- | :--- | :--- |
| `Type` | Must be `SMS` | Yes |
| `ApiUrl` | The REST API endpoint for sending SMS | Yes |
| `ApiKey` | The API Key or Auth Token | Yes |
| `FromNumber` | The phone number to send from | Yes |
| `DefaultToNumber` | The default recipient phone number | Yes |

### Full JSON Example

```json
{
  "AppSettings": {
    "Fusion": {
      "ElementList": [
        {
          "Name": "SystemEmail",
          "Manager": "NotificationElementManager",
          "Enable": true,
          "Properties": {
            "Type": "Email",
            "SmtpHost": "smtp.mailtrap.io",
            "SmtpPort": 2525,
            "Username": "your_username",
            "Password": "your_password",
            "FromAddress": "fusion@yourcompany.com",
            "DefaultRecipient": "devops@yourcompany.com",
            "DefaultSubject": "Fusion Critical Alert"
          }
        },
        {
          "Name": "OnCallSms",
          "Manager": "NotificationElementManager",
          "Enable": true,
          "Properties": {
            "Type": "SMS",
            "ApiUrl": "https://api.twilio.com/2010-04-01/Accounts/YOUR_SID/Messages.json",
            "ApiKey": "YOUR_AUTH_TOKEN",
            "FromNumber": "+15551234567",
            "DefaultToNumber": "+15559876543"
          }
        }
      ]
    }
  }
}
```

## Usage

Once configured, you can send notifications by publishing a message to the Message Bus using the element name as the topic.

**Topic:** `SystemEmail`  
**Payload:** `"The sorter has stopped unexpectedly."`

The `NotificationElementManager` will bond the payload to the specific element's `SendAsync` method.
