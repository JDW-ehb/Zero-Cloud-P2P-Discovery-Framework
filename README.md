# Zero-Cloud P2P Discovery Framework

A decentralized, cross-platform Peer-to-Peer (P2P) framework built with .NET MAUI & C#. The "Zero-Cloud" framework allows devices on the same local network to discover each other, communicate securely, share files, and chat without relying on external cloud infrastructure or centralized servers.

## Features

- **Peer-to-Peer Discovery:** Seamlessly discover and connect with other devices on the local network.
- **Zero-Trust P2P Security:** Fully encrypted communication using auto-generated self-signed RSA certificates, strict custom TLS validation, and Trust Groups.
- **Real-time P2P Messaging:** Direct, secure text communication between peers.
- **Secure File Sharing:** Directly share and receive files between trusted local peers.
- **LLM-Powered Chat capabilities:** Communicate with connected peers, featuring built-in Local LLM integrations.
- **Cross-Platform:** Built with .NET MAUI, offering support for Windows, macOS, Android, and iOS from a single codebase.

---

## Functionalities

### Peer Discovery & Trust Groups

Discovering local peers and establishing secure trust boundaries.

![Peer Discovery Demo](ZCM/Resources/Images/discovery.gif)

### Real-time P2P Messaging

Secure, real-time text communication directly between discovered peers on the local network.

![Messaging Demo](ZCM/Resources/Images/messaging.gif)

### Secure File Sharing

Sending and receiving files directly between peers without a central server.

![File Sharing Demo](ZCM/Resources/Images/filesharing.gif)

### Local LLM Chat

Messaging local peers using integrated LLM chat logic.

![LLM Chat Demo](ZCM/Resources/Images/llmchat.gif)


---

## Project Structure

The solution is divided into two main architectural components:

### 1. Zero Cloud Library (`ZCL`)

The core engine containing platform-agnostic business logic.

- **Security (`ZCL.Security`):** Handles cryptography, certificate generation (`TlsCertificateProvider`), custom TLS handshakes, and trust caches (`TrustGroupCache`).
- **Services (`ZCL.Services`):** Contains core networked services like the `FileSharingService` for transmitting file blobs and discovery protocol logic.

### 2. Zero Cloud MAUI (`ZCM`)

The cross-platform user interface application.

- **Pages (`ZCM.Pages`):** Contains XAML views like `MainPage`, `FileSharingPage`, `LLMChatPage`, and Popups.
- **ViewModels (`ZCM.ViewModels`):** MVVM data binding layer, including `FileSharinghubViewModel`, `LLMChatViewModel`, and data models like `SharedFileItem`.
- **Configuration:** MAUI App bootstrap and dependency injection rules (`MauiProgram.cs`).

---

## Getting Started

### Prerequisites

- [Visual Studio 2022/2026](https://visualstudio.microsoft.com/)
- .NET Multi-platform App UI (.NET MAUI) workload installed.
- Minimum Target Framework: `.NET 8.0` / `.NET 10.0` depending on your environment.

### Installation & Run

1. Clone the repository:
1. 2. Open the solution in Visual Studio.
3. Set the `ZCM` (.NET MAUI) project as the startup project.
4. Select your desired target emulatoror local machine (Windows Machine/Android Emulator/Mac Catalyst).
5. Build and Run (`F5`).

---

## Security Architecture

Security is handled locally without third-party Certificate Authorities (CAs). 
- **Identity:** Uniquely generated RSA (3072-bit) key-pairs provide distinct peer identities.
- **Network Secrets:** A pre-shared deterministic Network Secret (HMAC SHA256) establishes localized trust chains within specific user groups.
- **Ephemeral Keys:** PFX files are managed dynamically via exportable, self-signed certificates to maintain forward secrecy during rapid discovery cycles.

---

## Contributing

Contributions, issues, and feature requests are welcome! Feel free to check the [issues page](https://github.com/JDW-ehb/Zero-Cloud-P2P-Discovery-Framework/issues) if you want to contribute.

## License

This project is licensed under the [MIT License](LICENSE) - see the LICENSE file for details.
