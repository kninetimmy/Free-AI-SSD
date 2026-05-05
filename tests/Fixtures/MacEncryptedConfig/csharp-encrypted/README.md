# MAC5 cross-language encryption fixture

This directory holds an encrypted portable-config blob produced by the
Swift `SsdEncryption` port. The C# `MacEncryptedConfigCrossLanguageTests`
fixture asserts that `SsdEncryption.TryUnlockPortableConfig` unlocks this
blob with the password below and recovers the documented plaintext, and
the Swift test binary asserts the symmetric direction.

Password: `mac5-cross-lang-fixture-pw`
Expected `ollamaPort` after unlock: `13577`
Expected `models[0].name`: `llama3.2:3b`
Expected scheme: `aes-256-gcm+pbkdf2-sha256-v1`

To regenerate after a deliberate format change:

    swiftc mac-runner/Sources/SsdEncryption.swift \
           mac-runner/Tests/SsdEncryptionTests.swift \
           -parse-as-library -target arm64-apple-macos11.0 \
           -o /tmp/ssd-encryption-tests
    /tmp/ssd-encryption-tests write-fixture \
        tests/Fixtures/MacEncryptedConfig/csharp-encrypted

Refreshing must come with a dated decision in
`agent_docs/project_decisions.md` — the encrypted-config format is a
locked invariant.