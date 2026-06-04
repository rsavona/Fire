# Fortna Flexible Integration Routing Engine (Fire)

This document outlines the architecture for defining, creating, and managing all element interfaces within the Warehouse Control System.

## Overview

There are two main components:

1.  **`IElement`:** # Element Management System

This document outlines the architecture for defining, creating, and managing all hardware elements (e.g., printers, scanners, PLCs, scales) within the Warehouse Control System.

## Overview

The system is split into three primary components:

1.  **`Registrar`**: Responsible for scanning the assembly for managers and elements, and registering them with the Dependency Injection container.
2.  **`Manager`**: A singleton service that acts as a factory and supervisor for a specific type of element. It handles topic subscriptions and routes messages to the correct instances.
3.  **`Element`**: An interface to an external system or physical hardware. Elements are decoupled and communicate primarily through the message bus.

```mermaid
graph TD
    subgraph "Dependency Injection"
        Reg[ElementRegistrar] -- "Registers" --> EM[ElementManager]
    end

    subgraph "Core Services"
        Bus[IMessageBus]
        EM -- "Subscribes to Topics" --> Bus
    end

    subgraph "Element Suite"
        EM -- "Instantiates & Routes" --> E1[Element Instance A]
        EM -- "Instantiates & Routes" --> E2[Element Instance B]
    end

    E1 -- "Publishes Status/Data" --> Bus
    E2 -- "Publishes Status/Data" --> Bus
```

2.  **`ElementManager`**: A singleton service that acts as a registry and factory. It is responsible for loading all element configurations, instantiating the correct element objects, and providing other services access to them.

Other services (like an order fulfillment service) should **never** create a element instance directly. They should *always* ask the `ElementManager` for a element.

```mermaid
graph LR
    Service[Business Reaction] -- "Requests Element" --> EM[ElementManager]
    EM -- "Returns Reference" --> Service
    Service -- "Sends Command via Bus" --> Bus[Message Bus]
    Bus -- "Routes to" --> E[Element Instance]
```