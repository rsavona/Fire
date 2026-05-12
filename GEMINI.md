# Gemini Project Configuration

This file provides context to the Gemini CLI to help it understand the Fortna Fusion project.

## Project Overview

Fortna Fusion is a modular .NET 10 system designed for industrial automation and element orchestration. It manages communications and forces for logistics hardware, including PLCs, Zebra/JetMark printers, and ActiveMQ messaging, specifically focusing on "Print and Apply" automation bonds.

## Tech Stack

- **Language:** C# 14
- **Framework:** .NET 10.0
- **Project Types:** Console Applications and Class Libraries.
- **Messaging:** ActiveMQ
- **Testing:** xUnit
- **Key Libraries:** Castle.Core (DynamicProxy), Microsoft.Extensions.AI

## Key Commands

- **Build:** `dotnet build`
- **Run (Console):** `dotnet run --project DeviceSpace.Console`
- **Run (Tokamak):** `dotnet run --project Tokamak.AI` (AI-powered Blueprint Builder & Provisioner)
- **Test:** `dotnet test`
- **Clean:** `dotnet clean`

## File & Folder Structure

- `DeviceSpace.Common/`: Shared models, enums, exceptions, and core utilities (TCP, Logging).
- `DeviceSpace.Core/`: Core orchestration logic, message bus, and factory implementations.
- `Device.Plc.Suite/`: PLC-specific connectors, message parsing, and state machines.
- `Device.Printer.Suite/`: Zebra and JetMark printer integration and ZPL handling.
- `Device.ActiveMQ/`: ActiveMQ message consumer and manager logic.
- `Workflow.*/`: Specific business logic implementations (Forces).
- `Tokamak.AI/`: AI-driven Blueprint (.fusion) generator and hardware provisioner.
- `DeviceSpace.Console/`: Primary entry point for the application dashboard.

## Naming Conventions (Fusion)
- **Elements**: Hardware devices or external interfaces (formerly Devices).
- **Forces**: Business logic workflows and orchestrators (formerly Workflows).
- **Bonds**: Subscriptions and connections between elements and forces (formerly Routes).
- **Blueprints**: System configuration files using the `.fusion` extension (formerly Chamber files).

## Coding Style

- Use modern C# 14 features (Primary constructors, collection expressions, etc.).
- Maintain nullable reference types awareness.
- Follow the established pattern of "Registrar" classes for dependency injection/container setup.
