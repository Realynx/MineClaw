This self-signed localhost certificate and unencrypted key are public test fixtures only. They carry localhost and 127.0.0.1 SANs and are never used in production or included in deployment bundles.

Regenerate before expiry with OpenSSL:

    openssl req -x509 -nodes -newkey rsa:2048 -sha256 -days 3650 -subj "/CN=localhost/O=MineClaw test fixture only" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -out localhost-cert.pem -keyout localhost-key.pem
