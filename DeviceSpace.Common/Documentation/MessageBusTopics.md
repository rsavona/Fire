

| Queue Name                                              |          Description           |                     Direction |
|:--------------------------------------------------------|:------------------------------:|------------------------------:|
| DeviceStatusTopic3                                      | Queue for all status reporting |      Published by all devices |
| {PLCName},DecisionRequestMessage,{Decision Point}       |                                |       Published by PLC Device |
| {PLCName},DecisionUpdateMessage,{Decision Point}        |                                |       Published by PLC Device |                       **** |
| {PLCName},DecisionResponseMessage,{Decision Point}      |                                |      Subscribed by PLC Device |
| {ActiveMQName},ILabelRequestMessage,{ActiveMqQueueName} |                                | Subscribed by ActiveMQ Device |
| {ActiveMQName},ILabelDataMessage,{ActiveMqQueueName}    |                                |  Published by ActiveMQ Device |
| {PrinterName},LabelToPrintMessage                       |                                |   Subscribed By PrinterDevice | 
