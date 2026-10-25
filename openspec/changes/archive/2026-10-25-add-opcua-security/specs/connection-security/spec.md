## ADDED Requirements

### Requirement: Signed and encrypted OPC UA session
The bridge SHALL be able to connect to the OPC UA server with security policy Basic256Sha256 and message security mode SignAndEncrypt, presenting its own application instance certificate (NFR-09).

#### Scenario: Trusted client connects securely
- **WHEN** the bridge is configured for Basic256Sha256 SignAndEncrypt and its certificate is in the server's trusted list
- **THEN** the session is established with SignAndEncrypt and samples flow as before

#### Scenario: Server offers no matching endpoint
- **WHEN** the bridge is configured for Basic256Sha256 SignAndEncrypt and the server exposes only SecurityMode None
- **THEN** the bridge refuses to connect, does not fall back to None, and logs that no endpoint matches the required security

### Requirement: Untrusted certificates are rejected
The OPC UA server SHALL reject sessions from client certificates that are not in its trusted list, and the bridge SHALL reject server certificates that are not in its trusted list (NFR-09).

#### Scenario: Untrusted client certificate
- **WHEN** a client with a certificate that is not in the server's trusted list opens a secure session
- **THEN** the connection fails with a certificate-untrusted error and no data is served

#### Scenario: Untrusted server certificate
- **WHEN** the bridge connects to a server whose certificate is not in the bridge's trusted list
- **THEN** the bridge rejects the connection and logs the server certificate's thumbprint so an operator can decide to trust it

#### Scenario: Trust is granted by moving the certificate
- **WHEN** an operator moves a rejected certificate into the trusted store and the connection is retried
- **THEN** the session is established without restarting either service

### Requirement: Insecure mode only in development
Security mode and policy SHALL be set by configuration, and the bridge MUST refuse to start with SecurityMode None unless its environment is Development (NFR-09).

#### Scenario: None in production
- **WHEN** the bridge starts with environment Production and security mode None
- **THEN** startup fails with a configuration error naming the security setting

#### Scenario: None in development
- **WHEN** the bridge starts with environment Development and security mode None
- **THEN** it starts and logs a warning that the connection is unsecured

### Requirement: No secrets in source control
Credentials, connection-string passwords and private keys MUST NOT be committed; services SHALL read them from environment variables or .NET user-secrets, and CI SHALL fail when a secret is detected in the repository (NFR-10).

#### Scenario: Secret committed by mistake
- **WHEN** a commit adds a file containing a password or private key
- **THEN** the CI secret scan fails the build and names the file

#### Scenario: Fresh clone runs with supplied secrets
- **WHEN** a fresh clone is started with secrets provided only through `deploy/.env` and user-secrets
- **THEN** the whole stack starts and no tracked file contains a credential
