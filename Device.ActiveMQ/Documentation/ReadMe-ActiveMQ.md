# Device.ActiveMQ

This library provides a thread-safe wrapper for integrating ActiveMQ into a warehouse control system. It manages connections, session pooling, and message routing.

## Core Components

| Class | Description                                                                                                               |
| :--- |:--------------------------------------------------------------------------------------------------------------------------|
| **ActiveMqManager** | One ActiveMQManager per application that manages connections to multiple Broker. All messages funnel through the manager. |
| **SessionConsumerManager** | Manages a pool of active sessions and consumers, ensuring high throughput and resource efficiency.                        |
| **SessionConsumer** | A low-level wrapper for an individual ActiveMQ session that listens for messages and executes callbacks.                  |
| **ActiveMqDevice** | Represents a logical device or communication endpoint that interacts with the ActiveMQ broker.                            |
| **ActiveMqRegistrar** | Helper that has a factory method for creating ActiveMQ Devices                                                            |

## Features

* **Automatic Reconnection:** Built-in logic to monitor connection health and restore sessions if the broker becomes unavailable.
* **Thread-Safe Routing:** Uses concurrent collections to manage message dispatching safely across multiple threads.

## Usage 
### Add a device 
Under "DeviceList" in the 'YOURSPACE'.json add a new device with
* Manager: "ActiveMQManager"
* Name: <i>name your Active MQ Instance</i>
* Enabled: true
* ConnectionString: <i>your connection string</i>
* DoubleQueue: if set to true every queue the system writes to and reads from it will also write every message it writes or consumes (reads) into another queue named as the original queue with a 2 concatanated to the end. This aides in testing where the messages are consumed. There will be an unconsumed copy to examine. 

```
  "DeviceList": [
        {
          "Name": "TRGTBROKER",
          "Manager": "ActiveMQManager",
          "Enable": true,
          "Properties": {
            "ConnectionString": "tcp://127.0.0.1:61616?useKeepAlive=true",
            "DoubleQueue": true
            }
        },
````
### Add a route
Under the appropriate Workflow Element add a route to the "Routes" List. 
The configuration for a route is
* Mode: 
  * 0 is disabled
  * 1 the Handler is a named method in the workflow class
  * 2 (future) the Handler is a stored proceudre 
  * 3 (future) the Handler is a path to a Rosyln C# script
* Source: The Message Bus topic to read from
* Handler: The code to be ran on the message from the source
* Destination: The Message Bus topic to send the outcome of the handler to
```
"Routes": [
            {
              "Mode": 1,
              "Source": "TPNA2.DReqM.PNALINE-2",
              "Handler": "HandlePrintersToUseAsync",
              "Destination": "TPNA2.DRespM.PNALINE-2"
            },
```
### How a route is handlered by an ActiveMQ device
Knowledge Refresher
* Routes have 2 topics (Source and Destination) and a handler. 
* Topics have 3 parts, DeviceName, MessageType of the payload, Discriminator. 

ActiveMQ Devices are responsible for any leg of a route that starts with its DeviceName.
* Sources - when a source begins with the Active MQ's DeviceName 
  * the Active MQ Device subscribes to the MessageBus topic <i>"Source"</i> 
  * All messages from that topic are sent to the queue <i>Source.Discriminator</i> in the external MQ. 
  * Messages from the MessageBus are removed from their envelope and only the payload is sent to the external MQ. 
* Destinations - when a destination begins with the Active MQ's DeviceName 
  * the Active MQ Device will sunscribe to the queue <i>"Destination.Discriminator"</i> in the External MQ
  * All messages to that queue are sent to the MessageQueue topic <i>"Destination"</i>. 
  * Message from the external MQ are placed in an envelope before they are sent to the MessageBus.

