

# Message Bus Topics

```mermaid
sequenceDiagram
    participant PLC as PLC Element
    participant Bus as Message Bus
    participant RX as Reaction (Logic)
    participant MQ as ActiveMQ Element

    PLC->>Bus: DecisionRequestMessage
    Bus->>RX: [Subscribe] DecisionRequest
    RX->>Bus: ILabelRequestMessage
    Bus->>MQ: [Subscribe] LabelRequest
    MQ-->>Bus: ILabelDataMessage
    Bus-->>RX: [Subscribe] LabelData
    RX->>Bus: DecisionResponseMessage
    Bus->>PLC: [Subscribe] DecisionResponse
```

| Queue Name                                              |          Description           |                     Direction |
|:--------------------------------------------------------|:------------------------------:|------------------------------:|
| ElementStatusTopic3                                      | Queue for all status reporting |      Published by all elements |
| {PLCName},DecisionRequestMessage,{Decision Point}       |                                |       Published by PLC Element |
| {PLCName},DecisionUpdateMessage,{Decision Point}        |                                |       Published by PLC Element |                       **** |
| {PLCName},DecisionResponseMessage,{Decision Point}      |                                |      Subscribed by PLC Element |
| {ActiveMQName},ILabelRequestMessage,{ActiveMqQueueName} |                                | Subscribed by ActiveMQ Element |
| {ActiveMQName},ILabelDataMessage,{ActiveMqQueueName}    |                                |  Published by ActiveMQ Element |
| {PrinterName},LabelToPrintMessage                       |                                |   Subscribed By PrinterElement | 
