/**
 * Chat attachment data shared by the composer and the message list. Pure (no React Native),
 * so it can be unit tested with node:test.
 *
 * A sent attachment is an AG-UI input part whose source is a `botlan-upload://<id>` URL.
 * The bytes stay on the server (`/api/uploads`); the server expands references into image
 * data / inline text only for the model call, so saved conversations stay small.
 */
export const UPLOAD_SCHEME = "botlan-upload://";
export const IMAGE_MAX_BYTES = 10 * 1024 * 1024;
export const FILE_MAX_BYTES = 20 * 1024 * 1024;
export const MAX_ATTACHMENTS = 8;
export const IMAGE_MAX_EDGE = 1568;

export type AttachmentKind = "image" | "text" | "pdf" | "file";
export interface UploadInfo {
  id: string;
  name: string;
  mimeType: string;
  size: number;
  kind: AttachmentKind;
}
export interface AttachmentPart {
  type: "image" | "document";
  source: { type: "url"; value: string; mimeType: string };
  metadata: { name: string; size: number; kind: AttachmentKind };
}
export type UserContent = string | ({ type: "text"; text: string } | AttachmentPart | { type: string })[];

const EXECUTABLE =
  /\.(exe|dll|msi|msp|msix|appx|bat|cmd|com|scr|pif|cpl|ps1|psm1|vbs|vbe|wsf|wsh|hta|jar|apk|app|dmg|pkg|deb|rpm|lnk|reg|sys|drv|ocx|elf|so|dylib|run|bin)$/i;
const IMAGE_EXT = /\.(png|jpe?g|gif|webp|bmp|heic|heif|avif)$/i;

export const isImageFile = (name: string, mimeType?: string) =>
  (mimeType ?? "").startsWith("image/") ? !/svg/i.test(mimeType ?? "") : IMAGE_EXT.test(name);

export type RejectReason = "executable" | "imageTooLarge" | "fileTooLarge" | "tooMany" | "empty";

/** Client-side pre-checks; the server re-validates everything with content sniffing. */
export function checkFile(
  file: { name: string; size: number; mimeType?: string },
  alreadyAttached: number,
): RejectReason | undefined {
  if (alreadyAttached >= MAX_ATTACHMENTS) return "tooMany";
  if (EXECUTABLE.test(file.name) || /x-msdownload|x-msdos|x-executable|x-sh$/i.test(file.mimeType ?? ""))
    return "executable";
  if (!file.size) return "empty";
  if (file.size > FILE_MAX_BYTES) return "fileTooLarge";
  // Large images are downscaled before upload on web; this checks the final size.
  return undefined;
}

export function toPart(upload: UploadInfo): AttachmentPart {
  return {
    type: upload.kind === "image" ? "image" : "document",
    source: { type: "url", value: `${UPLOAD_SCHEME}${upload.id}`, mimeType: upload.mimeType },
    metadata: { name: upload.name, size: upload.size, kind: upload.kind },
  };
}

/** The user turn: the typed text first, then attachment references. */
export function buildUserContent(text: string, parts: AttachmentPart[]): UserContent {
  if (!parts.length) return text;
  return [...(text ? [{ type: "text" as const, text }] : []), ...parts];
}

export interface ShownAttachment extends UploadInfo {
  image: boolean;
}
/** Split saved user content into its text and the attachments to show in the bubble. */
export function splitUserContent(content: unknown): { text: string; attachments: ShownAttachment[] } {
  if (typeof content === "string") return { text: content, attachments: [] };
  if (!Array.isArray(content)) return { text: "", attachments: [] };
  const text: string[] = [];
  const attachments: ShownAttachment[] = [];
  for (const part of content as Record<string, unknown>[]) {
    if (!part || typeof part !== "object") continue;
    if (part.type === "text" && typeof part.text === "string") text.push(part.text);
    const source = part.source as { type?: string; value?: unknown; mimeType?: unknown } | undefined;
    if ((part.type === "image" || part.type === "document") && typeof source?.value === "string" &&
      source.value.startsWith(UPLOAD_SCHEME)) {
      const meta = (part.metadata ?? {}) as { name?: unknown; size?: unknown; kind?: unknown };
      const kind = (["image", "text", "pdf", "file"] as const).find((k) => k === meta.kind) ??
        (part.type === "image" ? "image" : "file");
      attachments.push({
        id: source.value.slice(UPLOAD_SCHEME.length),
        name: typeof meta.name === "string" ? meta.name : "file",
        size: typeof meta.size === "number" ? meta.size : 0,
        mimeType: typeof source.mimeType === "string" ? source.mimeType : "application/octet-stream",
        kind,
        image: part.type === "image",
      });
    }
  }
  return { text: text.join("\n\n"), attachments };
}

export function sizeLabel(size: number) {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${Math.round(size / 1024)} KB`;
  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

/** Long edge capped at max, aspect kept; returns the input when already small enough. */
export function fitWithin(width: number, height: number, max = IMAGE_MAX_EDGE) {
  const edge = Math.max(width, height);
  if (edge <= max) return { width, height, scaled: false };
  const scale = max / edge;
  return { width: Math.round(width * scale), height: Math.round(height * scale), scaled: true };
}
