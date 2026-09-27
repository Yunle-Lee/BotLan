import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";

function decodeKey(key: string): Buffer {
  const bytes = Buffer.from(key, "base64");
  if (bytes.length !== 32 || bytes.toString("base64") !== key) {
    throw new Error("Credential encryption requires a 32-byte base64 key");
  }
  return bytes;
}

/** Versioned AES-256-GCM envelope: version.nonce.tag.ciphertext. */
export function encryptSecret(plaintext: string, key: string): string {
  const nonce = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", decodeKey(key), nonce);
  cipher.setAAD(Buffer.from("agent:credential:v1"));
  const ciphertext = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  return [
    "v1",
    nonce.toString("base64url"),
    cipher.getAuthTag().toString("base64url"),
    ciphertext.toString("base64url"),
  ].join(".");
}

export function decryptSecret(encrypted: string, key: string): string {
  const keyBytes = decodeKey(key);
  const [version, nonceString, tagString, ciphertextString, extra] = encrypted.split(".");
  if (
    version !== "v1" ||
    nonceString === undefined ||
    tagString === undefined ||
    ciphertextString === undefined ||
    extra !== undefined
  ) {
    throw new Error("Invalid encrypted secret");
  }
  const encoded = [nonceString, tagString, ciphertextString];
  const [nonce, tag, ciphertext] = encoded.map((value) => Buffer.from(value, "base64url"));
  if (
    nonce.length !== 12 ||
    tag.length !== 16 ||
    encoded.some(
      (value, index) => Buffer.from(value, "base64url").toString("base64url") !== encoded[index],
    )
  ) {
    throw new Error("Invalid encrypted secret");
  }
  for (const aad of ["agent:credential:v1", "studio:credential:v1"]) {
    try {
      const decipher = createDecipheriv("aes-256-gcm", keyBytes, nonce);
      decipher.setAAD(Buffer.from(aad));
      decipher.setAuthTag(tag);
      return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
    } catch {}
  }
  throw new Error("Unable to authenticate or decrypt credential");
}
