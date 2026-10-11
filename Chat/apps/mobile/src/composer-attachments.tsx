import * as DocumentPicker from "expo-document-picker";
import { AlertCircle, FileText, FolderOpen, Paperclip, Upload, X } from "lucide-react-native";
import { type RefObject, useCallback, useEffect, useRef, useState } from "react";
import { ActivityIndicator, Image, Modal, Platform, Pressable, Text, View } from "react-native";
import { API_URL, type MuseApi } from "./api";
import {
  type AttachmentPart,
  checkFile,
  fitWithin,
  IMAGE_MAX_BYTES,
  IMAGE_MAX_EDGE,
  isImageFile,
  MAX_ATTACHMENTS,
  type RejectReason,
  type ShownAttachment,
  sizeLabel,
  toPart,
  type UploadInfo,
} from "./attachment-parts";
import { useI18n } from "./i18n";
import { colors, s } from "./ui";

type Pending = {
  localId: string;
  name: string;
  size: number;
  image: boolean;
  preview?: string;
  status: "uploading" | "done" | "error";
  progress: number;
  error?: string;
  upload?: UploadInfo;
};
/** A file chosen on web (File) or native (picker asset with a local URI). */
type Picked = { name: string; size: number; mimeType?: string; file?: Blob; uri?: string };

const ACCEPT =
  "image/*,.pdf,.txt,.md,.markdown,.csv,.tsv,.json,.jsonl,.xml,.yaml,.yml,.toml,.ini,.log,.html,.css," +
  ".js,.mjs,.ts,.tsx,.jsx,.py,.rb,.go,.rs,.java,.kt,.c,.h,.cpp,.hpp,.cs,.swift,.php,.sql,.sh,.vue,.svelte," +
  ".docx,.xlsx,.pptx,.zip";

/** Web: re-encode to JPEG (long edge ≤ 1568 px, quality 0.85) when large or not PNG/JPEG. */
async function prepareImage(file: Blob, name: string): Promise<{ blob: Blob; name: string }> {
  if (Platform.OS !== "web" || typeof document === "undefined") return { blob: file, name };
  const type = file.type;
  // llama-server (stb_image) decodes PNG/JPEG/GIF; WebP, BMP, HEIC and AVIF are converted.
  const keep = /^image\/(png|jpeg|gif)$/.test(type) && file.size <= 1.5 * 1024 * 1024;
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    return { blob: file, name };
  }
  const fit = fitWithin(bitmap.width, bitmap.height, IMAGE_MAX_EDGE);
  if (keep && !fit.scaled) {
    bitmap.close();
    return { blob: file, name };
  }
  const canvas = document.createElement("canvas");
  canvas.width = fit.width;
  canvas.height = fit.height;
  const context = canvas.getContext("2d");
  if (!context) return { blob: file, name };
  context.fillStyle = "#FFFFFF"; // JPEG has no alpha.
  context.fillRect(0, 0, fit.width, fit.height);
  context.drawImage(bitmap, 0, 0, fit.width, fit.height);
  bitmap.close();
  const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.85));
  if (!blob) return { blob: file, name };
  return { blob, name: name.replace(/\.[^.]+$/, "") + ".jpg" };
}

function uploadWithProgress(
  api: MuseApi,
  form: FormData,
  onProgress: (fraction: number) => void,
): Promise<UploadInfo> {
  if (Platform.OS !== "web" || typeof XMLHttpRequest === "undefined")
    return api.request<UploadInfo>("/api/uploads", form);
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${API_URL}/api/uploads`);
    xhr.setRequestHeader("Authorization", `Bearer ${api.token}`);
    xhr.upload.onprogress = (event) => event.lengthComputable && onProgress(event.loaded / event.total);
    xhr.onerror = () => reject(new Error("Network error"));
    xhr.onload = () => {
      let payload: { error?: unknown } & Partial<UploadInfo> = {};
      try {
        payload = JSON.parse(xhr.responseText);
      } catch {}
      if (xhr.status >= 200 && xhr.status < 300) resolve(payload as UploadInfo);
      else reject(new Error(typeof payload.error === "string" ? payload.error : `Upload failed (${xhr.status})`));
    };
    xhr.send(form);
  });
}

/** Composer attachment state: pick, paste, drop, upload, and the parts to send. */
export function useComposerAttachments(api: MuseApi, enabled: boolean) {
  const { t } = useI18n();
  const [items, setItems] = useState<Pending[]>([]);
  const [notice, setNotice] = useState("");
  const [dragging, setDragging] = useState(false);
  const count = useRef(0);
  count.current = items.length;
  const dropRef = useRef<View>(null);
  const patch = (localId: string, value: Partial<Pending>) =>
    setItems((list) => list.map((item) => (item.localId === localId ? { ...item, ...value } : item)));
  const reason = useCallback((r: RejectReason, name: string) =>
    t(`attach.reject.${r}`).replace("{name}", name).replace("{max}", String(MAX_ATTACHMENTS)), [t]);

  const add = useCallback(
    async (files: Picked[]) => {
      const problems: string[] = [];
      for (const picked of files) {
        const rejected = checkFile(picked, count.current);
        if (rejected) {
          problems.push(reason(rejected, picked.name));
          if (rejected === "tooMany") break;
          continue;
        }
        const localId = `att-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
        const image = isImageFile(picked.name, picked.mimeType);
        const preview = image
          ? picked.file && typeof URL !== "undefined" && URL.createObjectURL
            ? URL.createObjectURL(picked.file)
            : picked.uri
          : undefined;
        count.current++;
        setItems((list) => [...list, { localId, name: picked.name, size: picked.size, image, preview, status: "uploading", progress: 0 }]);
        void (async () => {
          try {
            const form = new FormData();
            if (picked.file) {
              const { blob, name } = image ? await prepareImage(picked.file, picked.name) : { blob: picked.file, name: picked.name };
              if (image && blob.size > IMAGE_MAX_BYTES) throw new Error(reason("imageTooLarge", picked.name));
              patch(localId, { size: blob.size, name });
              form.append("file", blob, name);
            } else if (picked.uri) {
              if (image && picked.size > IMAGE_MAX_BYTES) throw new Error(reason("imageTooLarge", picked.name));
              // React Native's FormData file shape.
              form.append("file", { uri: picked.uri, name: picked.name, type: picked.mimeType || "application/octet-stream" } as unknown as Blob);
            }
            const upload = await uploadWithProgress(api, form, (progress) => patch(localId, { progress }));
            patch(localId, { status: "done", progress: 1, upload, image: upload.kind === "image", size: upload.size });
          } catch (e) {
            patch(localId, { status: "error", error: e instanceof Error ? e.message : String(e) });
          }
        })();
      }
      setNotice(problems.join(" "));
    },
    [api, reason],
  );

  const pick = useCallback(async () => {
    if (Platform.OS === "web" && typeof document !== "undefined") {
      const input = document.createElement("input");
      input.type = "file";
      input.multiple = true;
      input.accept = ACCEPT;
      input.onchange = () => {
        const list = Array.from(input.files ?? []);
        void add(list.map((file) => ({ name: file.name, size: file.size, mimeType: file.type, file })));
      };
      input.click();
      return;
    }
    const result = await DocumentPicker.getDocumentAsync({ multiple: true, copyToCacheDirectory: true });
    if (result.canceled) return;
    void add(result.assets.map((asset) => ({ name: asset.name, size: asset.size ?? 0, mimeType: asset.mimeType, uri: asset.uri, file: asset.file })));
  }, [add]);

  // Web: paste images/files from the clipboard, and drag & drop onto the chat area.
  useEffect(() => {
    if (Platform.OS !== "web" || !enabled || typeof document === "undefined") return;
    const filesOf = (list?: FileList | null) =>
      Array.from(list ?? []).map((file) => ({
        name: file.name || `pasted-${Date.now()}.${(file.type.split("/")[1] || "png").replace("jpeg", "jpg")}`,
        size: file.size,
        mimeType: file.type,
        file,
      }));
    const onPaste = (event: ClipboardEvent) => {
      const files = filesOf(event.clipboardData?.files);
      if (!files.length) return; // plain text paste stays in the input
      event.preventDefault();
      void add(files);
    };
    document.addEventListener("paste", onPaste);
    const node = dropRef.current as unknown as HTMLElement | null;
    const hasFiles = (event: DragEvent) => Array.from(event.dataTransfer?.types ?? []).includes("Files");
    let depth = 0;
    const onEnter = (event: DragEvent) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      depth++;
      setDragging(true);
    };
    const onOver = (event: DragEvent) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = "copy";
    };
    const onLeave = (event: DragEvent) => {
      if (!hasFiles(event)) return;
      depth = Math.max(0, depth - 1);
      if (!depth) setDragging(false);
    };
    const onDrop = (event: DragEvent) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      depth = 0;
      setDragging(false);
      void add(filesOf(event.dataTransfer?.files));
    };
    node?.addEventListener?.("dragenter", onEnter);
    node?.addEventListener?.("dragover", onOver);
    node?.addEventListener?.("dragleave", onLeave);
    node?.addEventListener?.("drop", onDrop);
    return () => {
      document.removeEventListener("paste", onPaste);
      node?.removeEventListener?.("dragenter", onEnter);
      node?.removeEventListener?.("dragover", onOver);
      node?.removeEventListener?.("dragleave", onLeave);
      node?.removeEventListener?.("drop", onDrop);
    };
  }, [add, enabled]);

  const remove = useCallback((localId: string) => {
    setItems((list) => {
      const item = list.find((i) => i.localId === localId);
      if (item?.preview?.startsWith("blob:")) URL.revokeObjectURL(item.preview);
      return list.filter((i) => i.localId !== localId);
    });
  }, []);
  /** Clears the composer; local previews are kept alive for the sent bubble until reload. */
  const clear = useCallback(() => {
    setItems([]);
    setNotice("");
  }, []);
  const parts: AttachmentPart[] = items.flatMap((item) => (item.status === "done" && item.upload ? [toPart(item.upload)] : []));
  return {
    items,
    parts,
    uploading: items.some((item) => item.status === "uploading"),
    failed: items.some((item) => item.status === "error"),
    notice,
    dragging,
    dropRef: dropRef as RefObject<View>,
    pick,
    remove,
    clear,
  };
}
export type ComposerAttachments = ReturnType<typeof useComposerAttachments>;

/** "+" menu: upload from this device, or attach a document already in Files. */
export function AttachMenu({
  open,
  onClose,
  onUpload,
  onWorkspace,
}: {
  open: boolean;
  onClose: () => void;
  onUpload: () => void;
  onWorkspace: () => void;
}) {
  const { t } = useI18n();
  if (!open) return null;
  const row = (label: string, Icon: typeof Upload, onPress: () => void) => (
    <Pressable
      accessibilityRole="menuitem"
      accessibilityLabel={label}
      onPress={() => {
        onClose();
        onPress();
      }}
      style={({ pressed, hovered }: { pressed: boolean; hovered?: boolean }) => [
        s.row,
        { gap: 10, paddingHorizontal: 14, paddingVertical: 11, borderRadius: 14, backgroundColor: pressed || hovered ? colors.sky : "transparent" },
      ]}
    >
      <Icon size={17} color={colors.blueDark} />
      <Text style={[s.text, { fontSize: 14 }]}>{label}</Text>
    </Pressable>
  );
  return (
    <View
      accessibilityRole="menu"
      style={{
        alignSelf: "flex-start",
        marginBottom: 8,
        padding: 6,
        borderRadius: 18,
        backgroundColor: "#FFF",
        borderWidth: 1,
        borderColor: colors.line,
        shadowColor: "#18384B",
        shadowOpacity: 0.1,
        shadowRadius: 16,
        shadowOffset: { width: 0, height: 4 },
        elevation: 4,
      }}
    >
      {row(t("attach.upload"), Upload, onUpload)}
      {row(t("attach.workspace"), FolderOpen, onWorkspace)}
    </View>
  );
}

/** Chips above the input: thumbnails / file name + size, progress, error, remove. */
export function AttachmentChips({ state }: { state: ComposerAttachments }) {
  const { t } = useI18n();
  if (!state.items.length && !state.notice) return null;
  return (
    <View style={{ paddingHorizontal: 9, paddingTop: 6, gap: 6 }}>
      {!!state.items.length && (
        <View style={[s.row, { gap: 6, flexWrap: "wrap" }]}>
          {state.items.map((item) => {
            const failed = item.status === "error";
            const label = `${item.name}, ${sizeLabel(item.size)}${
              item.status === "uploading" ? `, ${t("attach.uploading")}` : failed ? `, ${t("attach.failed")}: ${item.error}` : ""
            }`;
            return (
              <View
                key={item.localId}
                accessible
                accessibilityLabel={label}
                style={[
                  s.row,
                  {
                    gap: 7,
                    maxWidth: 240,
                    backgroundColor: failed ? "#FBEDEC" : colors.sky,
                    borderRadius: 14,
                    padding: item.image && item.preview ? 4 : 8,
                    paddingRight: 4,
                  },
                ]}
              >
                {item.image && item.preview ? (
                  <Image source={{ uri: item.preview }} style={{ width: 40, height: 40, borderRadius: 10, backgroundColor: colors.line }} accessibilityIgnoresInvertColors />
                ) : failed ? (
                  <AlertCircle size={15} color={colors.danger} />
                ) : (
                  <FileText size={15} color={colors.blueDark} />
                )}
                <View style={{ flexShrink: 1, gap: 2 }}>
                  <Text numberOfLines={1} style={{ fontSize: 12, color: colors.text }}>
                    {item.name}
                  </Text>
                  <Text numberOfLines={2} style={{ fontSize: 10.5, color: failed ? colors.danger : colors.muted }}>
                    {failed ? item.error : item.status === "uploading" ? `${t("attach.uploading")} ${Math.round(item.progress * 100)}%` : sizeLabel(item.size)}
                  </Text>
                  {item.status === "uploading" && (
                    <View style={{ height: 3, borderRadius: 2, backgroundColor: "#D6E9F6", overflow: "hidden" }}>
                      <View style={{ height: 3, width: `${Math.max(8, item.progress * 100)}%`, backgroundColor: colors.blueDark }} />
                    </View>
                  )}
                </View>
                {item.status === "uploading" && <ActivityIndicator size="small" color={colors.blueDark} />}
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={`${t("attach.remove")}: ${item.name}`}
                  hitSlop={8}
                  onPress={() => state.remove(item.localId)}
                  style={{ padding: 6 }}
                >
                  <X size={13} color={colors.muted} />
                </Pressable>
              </View>
            );
          })}
        </View>
      )}
      {!!state.notice && (
        <Text accessibilityLiveRegion="polite" style={{ fontSize: 12, color: colors.danger }}>
          {state.notice}
        </Text>
      )}
    </View>
  );
}

export function DropOverlay({ visible }: { visible: boolean }) {
  const { t } = useI18n();
  if (!visible) return null;
  return (
    <View
      pointerEvents="none"
      accessibilityLiveRegion="polite"
      style={{
        position: "absolute",
        top: 8,
        left: 8,
        right: 8,
        bottom: 8,
        zIndex: 20,
        borderRadius: 24,
        borderWidth: 2,
        borderStyle: "dashed",
        borderColor: colors.blueDark,
        backgroundColor: "rgba(237,247,253,0.92)",
        alignItems: "center",
        justifyContent: "center",
        gap: 10,
      }}
    >
      <Paperclip size={30} color={colors.blueDark} />
      <Text style={[s.text, { fontSize: 16, color: colors.blueDark }]}>{t("attach.drop")}</Text>
    </View>
  );
}

// Signed content URLs last 15 minutes; refresh a little earlier.
const signedCache = new Map<string, { url: string; at: number }>();
function useUploadUrl(api: MuseApi, id: string, enabled: boolean) {
  const cached = signedCache.get(id);
  const [url, setUrl] = useState(cached && Date.now() - cached.at < 10 * 60 * 1000 ? cached.url : "");
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (!enabled || url) return;
    let live = true;
    api.request<{ url: string }>(`/api/uploads/${encodeURIComponent(id)}`).then(
      ({ url: signed }) => {
        signedCache.set(id, { url: signed, at: Date.now() });
        if (live) setUrl(signed);
      },
      () => live && setFailed(true),
    );
    return () => {
      live = false;
    };
  }, [api, id, enabled, url]);
  return { url, failed };
}

function ImageThumb({ api, item, onOpen }: { api: MuseApi; item: ShownAttachment; onOpen: (url: string) => void }) {
  const { t } = useI18n();
  const { url, failed } = useUploadUrl(api, item.id, true);
  return (
    <Pressable
      accessibilityRole="imagebutton"
      accessibilityLabel={`${t("attach.openImage")}: ${item.name}`}
      disabled={!url}
      onPress={() => onOpen(url)}
      style={{ width: 132, height: 132, borderRadius: 16, overflow: "hidden", backgroundColor: colors.line, alignItems: "center", justifyContent: "center" }}
    >
      {url ? (
        <Image source={{ uri: url }} style={{ width: 132, height: 132 }} resizeMode="cover" accessibilityIgnoresInvertColors />
      ) : failed ? (
        <Text style={[s.small, { textAlign: "center", padding: 8 }]}>{t("attach.unavailable")}</Text>
      ) : (
        <ActivityIndicator color={colors.blueDark} />
      )}
    </Pressable>
  );
}

/** Images and file chips in the user's own message bubble, with a simple lightbox. */
export function MessageAttachments({ api, attachments }: { api: MuseApi; attachments: ShownAttachment[] }) {
  const { t } = useI18n();
  const [open, setOpen] = useState("");
  if (!attachments.length) return null;
  const images = attachments.filter((a) => a.image);
  const others = attachments.filter((a) => !a.image);
  return (
    <View style={{ gap: 6, alignItems: "flex-end" }}>
      {!!images.length && (
        <View style={[s.row, { gap: 6, flexWrap: "wrap", justifyContent: "flex-end" }]}>
          {images.map((item) => (
            <ImageThumb key={item.id} api={api} item={item} onOpen={setOpen} />
          ))}
        </View>
      )}
      {others.map((item) => (
        <View
          key={item.id}
          accessible
          accessibilityLabel={`${t("attach.file")}: ${item.name}, ${sizeLabel(item.size)}`}
          style={[s.row, { gap: 8, maxWidth: 280, backgroundColor: colors.sky, borderRadius: 14, paddingHorizontal: 11, paddingVertical: 9 }]}
        >
          <FileText size={16} color={colors.blueDark} />
          <View style={{ flexShrink: 1 }}>
            <Text numberOfLines={1} style={{ fontSize: 13, color: colors.text }}>
              {item.name}
            </Text>
            <Text style={{ fontSize: 11, color: colors.muted }}>{sizeLabel(item.size)}</Text>
          </View>
        </View>
      ))}
      <Modal transparent visible={!!open} animationType="fade" onRequestClose={() => setOpen("")}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("attach.closeImage")}
          onPress={() => setOpen("")}
          style={{ flex: 1, backgroundColor: "rgba(10,16,20,0.86)", alignItems: "center", justifyContent: "center", padding: 24 }}
        >
          {!!open && (
            <Image
              source={{ uri: open }}
              style={{ width: "100%", height: "100%" }}
              resizeMode="contain"
              accessibilityIgnoresInvertColors
            />
          )}
          <View style={{ position: "absolute", top: 18, right: 18, padding: 10, borderRadius: 20, backgroundColor: "rgba(255,255,255,0.15)" }}>
            <X size={22} color="#FFF" />
          </View>
        </Pressable>
      </Modal>
    </View>
  );
}
