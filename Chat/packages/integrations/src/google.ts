import { randomUUID } from "node:crypto";
import { type DefaultTreeAdapterMap, parseFragment } from "parse5";
import { z } from "zod";
import {
  type CalendarEvent,
  type EmailDraft,
  type EventDraft,
  emailDraftSchema,
  eventDraftSchema,
  type Mail,
} from "../../domain/src/index.ts";

const GMAIL = "https://gmail.googleapis.com/gmail/v1/users/me";
const CALENDAR = "https://www.googleapis.com/calendar/v3";
export const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;
export const MAX_TOTAL_ATTACHMENT_BYTES = 20 * 1024 * 1024;
const MAX_JSON_BYTES = Math.ceil((MAX_ATTACHMENT_BYTES * 4) / 3) + 1024 * 1024;

export class OutcomeUnknownError extends Error {
  readonly code = "outcome_unknown";
  constructor(
    message = "Google may have completed this action. Check Google before trying again.",
  ) {
    super(message);
    this.name = "OutcomeUnknownError";
  }
}

export class GoogleApiError extends Error {
  constructor(
    readonly status: number,
    detail: string,
  ) {
    super(`Google API (${status}): ${detail}`);
    this.name = "GoogleApiError";
  }
}

export class RecurringEventError extends Error {
  readonly status = 422;
  constructor() {
    super(
      "Recurring events cannot be changed here yet. Open Google Calendar to choose one occurrence or the whole series.",
    );
    this.name = "RecurringEventError";
  }
}

export interface CalendarListEntry {
  id: string;
  name: string;
  timeZone: string;
  accessRole: string;
}
export interface ListEventsOptions {
  calendarId?: string;
  timeMin?: string;
  timeMax?: string;
}

interface GmailPart {
  mimeType?: string;
  filename?: string;
  headers?: { name: string; value: string }[];
  body?: { data?: string; size?: number; attachmentId?: string };
  parts?: GmailPart[];
}
const partSchema: z.ZodType<GmailPart> = z.lazy(() =>
  z.object({
    mimeType: z.string().optional(),
    filename: z.string().optional(),
    headers: z.array(z.object({ name: z.string(), value: z.string() })).optional(),
    body: z
      .object({
        data: z.string().optional(),
        size: z.number().nonnegative().optional(),
        attachmentId: z.string().optional(),
      })
      .optional(),
    parts: z.array(partSchema).optional(),
  }),
);
const messageSchema = z.object({
  id: z.string().min(1),
  threadId: z.string().min(1),
  snippet: z.string().default(""),
  internalDate: z.string().optional(),
  labelIds: z.array(z.string()).default([]),
  payload: partSchema.optional(),
});
const googleEventSchema = z.object({
  id: z.string().min(1),
  etag: z.string().optional(),
  recurrence: z.array(z.string()).optional(),
  recurringEventId: z.string().optional(),
  summary: z.string().default("(Untitled event)"),
  start: z.object({
    date: z.string().optional(),
    dateTime: z.string().optional(),
    timeZone: z.string().optional(),
  }),
  end: z.object({ date: z.string().optional(), dateTime: z.string().optional() }),
  location: z.string().default(""),
  description: z.string().default(""),
  attendees: z.array(z.object({ email: z.string() })).default([]),
});
export interface MailAttachment {
  name: string;
  mimeType: string;
  bytes: Uint8Array;
}

function idPath(id: string): string {
  if (!/^[A-Za-z0-9_-]+$/.test(id)) throw new Error("Invalid Google resource ID");
  return encodeURIComponent(id);
}
function calendarPath(calendarId: string): string {
  if (
    !calendarId ||
    calendarId.length > 1024 ||
    Array.from(calendarId).some((char) => char.charCodeAt(0) <= 32 || char.charCodeAt(0) === 127) ||
    calendarId === "." ||
    calendarId === ".."
  )
    throw new Error("Invalid Google calendar ID");
  return `${CALENDAR}/calendars/${encodeURIComponent(calendarId)}/events`;
}
function singleLine(value: string, field: string): string {
  if (Array.from(value).some((char) => char.charCodeAt(0) < 32 || char.charCodeAt(0) === 127))
    throw new Error(`Invalid ${field}: header control characters are not allowed`);
  return value;
}
function decodeBase64url(encoded: string, limit = MAX_ATTACHMENT_BYTES): Buffer {
  if (encoded.length > Math.ceil((limit * 4) / 3) + 4)
    throw new Error("Attachment or message body exceeds the size limit");
  if (!/^[A-Za-z0-9_-]*={0,2}$/.test(encoded))
    throw new Error("Invalid base64url attachment or message body");
  const bytes = Buffer.from(encoded, "base64url");
  if (bytes.length > limit) throw new Error("Attachment or message body is too large");
  if (bytes.toString("base64url") !== encoded.replace(/=+$/, ""))
    throw new Error("Invalid base64url attachment or message body");
  return bytes;
}
function headers(part?: GmailPart): Map<string, string> {
  return new Map((part?.headers ?? []).map(({ name, value }) => [name.toLowerCase(), value]));
}
function decodeHeader(value: string): string {
  return value
    .replace(/(\?=)[ \t]+(?==\?)/g, "$1")
    .replace(
      /=\?([^?]+)\?([bq])\?([^?]*)\?=/gi,
      (original, charset: string, encoding: string, text: string) => {
        try {
          const bytes =
            encoding.toLowerCase() === "b"
              ? Buffer.from(text, "base64")
              : Buffer.from(
                  text
                    .replace(/_/g, " ")
                    .replace(/=([0-9a-f]{2})/gi, (_, code: string) =>
                      String.fromCharCode(Number.parseInt(code, 16)),
                    ),
                  "latin1",
                );
          return new TextDecoder(charset).decode(bytes);
        } catch {
          return original;
        }
      },
    );
}
function decodeSnippet(value: string): string {
  const entities: Record<string, string> = {
    amp: "&",
    lt: "<",
    gt: ">",
    quot: '"',
    apos: "'",
    nbsp: " ",
  };
  return value.replace(
    /&(#x[\da-f]+|#\d+|amp|lt|gt|quot|apos|nbsp);/gi,
    (original, entity: string) => {
      if (!entity.startsWith("#")) return entities[entity.toLowerCase()] ?? original;
      const code =
        entity[1].toLowerCase() === "x"
          ? Number.parseInt(entity.slice(2), 16)
          : Number.parseInt(entity.slice(1), 10);
      return code >= 0 && code <= 0x10ffff ? String.fromCodePoint(code) : original;
    },
  );
}
function addresses(value: string): string[] {
  return value.match(/[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+/g) ?? [];
}
/** Extract text from a parsed HTML tree. Nothing is rendered or fetched. */
function htmlToPlainText(html: string): string {
  const root = parseFragment(html);
  const excluded = new Set([
    "script",
    "style",
    "head",
    "template",
    "noscript",
    "iframe",
    "object",
    "svg",
    "math",
  ]);
  const blocks = new Set([
    "address",
    "article",
    "aside",
    "blockquote",
    "div",
    "footer",
    "form",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
    "header",
    "hr",
    "li",
    "main",
    "ol",
    "p",
    "pre",
    "section",
    "table",
    "tr",
    "ul",
  ]);
  const stack: (DefaultTreeAdapterMap["node"] | string)[] = [root];
  const text: string[] = [];
  while (stack.length) {
    const node = stack.pop();
    if (node === undefined) break;
    if (typeof node === "string") {
      text.push(node);
      continue;
    }
    if ("value" in node) {
      text.push(node.value);
      continue;
    }
    if ("tagName" in node) {
      if (excluded.has(node.tagName)) continue;
      if (node.tagName === "br") {
        text.push("\n");
        continue;
      }
      if (blocks.has(node.tagName)) {
        text.push("\n");
        stack.push("\n");
      }
      if (node.tagName === "td" || node.tagName === "th") stack.push("\t");
    }
    if ("childNodes" in node) {
      for (let index = node.childNodes.length - 1; index >= 0; index--)
        stack.push(node.childNodes[index]);
    }
  }
  return text
    .join("")
    .replace(/[\t \u00a0]+/g, " ")
    .replace(/ *\n */g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}
function mapMessage(message: z.infer<typeof messageSchema>): Mail {
  const metadata = headers(message.payload);
  const plain: string[] = [];
  const html: string[] = [];
  const attachments: string[] = [];
  const visit = (part: GmailPart, depth: number) => {
    if (depth > 30) throw new Error("Gmail message MIME nesting exceeds the limit");
    if (part.filename && part.body?.attachmentId)
      attachments.push(
        `${message.id}:${part.body.attachmentId}:${encodeURIComponent(part.filename)}`,
      );
    if (
      !part.filename &&
      (part.mimeType === "text/plain" || part.mimeType === "text/html") &&
      part.body?.data
    ) {
      const charset =
        headers(part)
          .get("content-type")
          ?.match(/charset=["']?([^;"'\s]+)/i)?.[1] ?? "utf-8";
      const text = new TextDecoder(charset).decode(decodeBase64url(part.body.data, 1024 * 1024));
      if (part.mimeType === "text/plain") plain.push(text);
      else html.push(htmlToPlainText(text));
    }
    for (const child of part.parts ?? []) visit(child, depth + 1);
  };
  if (message.payload) visit(message.payload, 0);
  const from = decodeHeader(metadata.get("from") ?? "");
  const address = addresses(from)[0] ?? from;
  const sender = from.includes("<")
    ? from.slice(0, from.indexOf("<")).trim().replace(/^"|"$/g, "")
    : address;
  const time = message.internalDate
    ? Number(message.internalDate)
    : Date.parse(metadata.get("date") ?? "");
  const body = plain.length
    ? plain.join("\n\n")
    : html.length
      ? html.join("\n\n")
      : decodeSnippet(message.snippet);
  if (body.length > 1024 * 1024) throw new Error("Gmail message text exceeds the 1 MiB limit");
  return {
    id: message.id,
    threadId: message.threadId,
    from: address,
    sender,
    to: addresses(metadata.get("to") ?? ""),
    subject: decodeHeader(metadata.get("subject") ?? "(No subject)"),
    body,
    date: Number.isFinite(time) ? new Date(time).toISOString() : "",
    unread: message.labelIds.includes("UNREAD"),
    label: message.labelIds.includes("INBOX")
      ? "Inbox"
      : message.labelIds.includes("SENT")
        ? "Sent"
        : "Mail",
    attachments,
  };
}
function mapEvent(value: unknown, calendarId: string, calendarTimeZone = "UTC"): CalendarEvent {
  const event = googleEventSchema.parse(value);
  const allDay = Boolean(event.start.date);
  const start = allDay ? event.start.date : event.start.dateTime;
  const end = allDay ? event.end.date : event.end.dateTime;
  if (!start || !end || !Number.isFinite(Date.parse(start)) || !Number.isFinite(Date.parse(end)))
    throw new Error("Invalid Google event time range");
  return {
    id: event.id,
    calendarId,
    title: event.summary,
    start,
    end,
    allDay,
    timeZone: event.start.timeZone ?? calendarTimeZone,
    location: event.location,
    description: event.description,
    attendees: event.attendees.map((attendee) => attendee.email),
  };
}
function eventBody(draft: EventDraft, patch = false) {
  return {
    summary: draft.title,
    start: draft.allDay
      ? { date: draft.start, ...(patch ? { dateTime: null } : {}) }
      : { dateTime: draft.start, timeZone: draft.timeZone, ...(patch ? { date: null } : {}) },
    end: draft.allDay
      ? { date: draft.end, ...(patch ? { dateTime: null } : {}) }
      : { dateTime: draft.end, timeZone: draft.timeZone, ...(patch ? { date: null } : {}) },
    location: draft.location,
    description: draft.description,
    attendees: draft.attendees.map((email) => ({ email })),
  };
}
function encodedSubject(subject: string): string {
  const chunks: string[] = [];
  let chunk = "";
  for (const char of subject) {
    if (Buffer.byteLength(chunk + char) > 42) {
      chunks.push(chunk);
      chunk = "";
    }
    chunk += char;
  }
  if (chunk) chunks.push(chunk);
  return chunks.map((part) => `=?UTF-8?B?${Buffer.from(part).toString("base64")}?=`).join("\r\n ");
}
function wrapBase64(bytes: Uint8Array): string {
  return (
    Buffer.from(bytes)
      .toString("base64")
      .match(/.{1,76}/g)
      ?.join("\r\n") ?? ""
  );
}
function validateAttachments(attachments: MailAttachment[]): void {
  if (attachments.length > 10) throw new Error("Attachment count exceeds the limit of 10");
  let total = 0;
  for (const attachment of attachments) {
    singleLine(attachment.name, "attachment name");
    if (!attachment.name || Buffer.byteLength(attachment.name) > 180)
      throw new Error("Invalid attachment name length");
    if (!/^[A-Za-z0-9!#$&^_.+-]+\/[A-Za-z0-9!#$&^_.+-]+$/.test(attachment.mimeType))
      throw new Error("Invalid attachment MIME type");
    if (attachment.bytes.length > MAX_ATTACHMENT_BYTES)
      throw new Error("Attachment is too large (10 MiB limit)");
    total += attachment.bytes.length;
  }
  if (total > MAX_TOTAL_ATTACHMENT_BYTES)
    throw new Error("Total attachment size exceeds the 20 MiB limit");
}

async function readJson(response: Response): Promise<unknown> {
  if (Number(response.headers.get("content-length")) > MAX_JSON_BYTES) {
    await response.body?.cancel();
    throw new Error("Google response exceeds the size limit");
  }
  if (!response.body) throw new Error("Google returned an empty response");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      length += value.length;
      if (length > MAX_JSON_BYTES) {
        await reader.cancel();
        throw new Error("Google response exceeds the size limit");
      }
      chunks.push(value);
    }
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } finally {
    reader.releaseLock();
  }
}

export class GoogleClient {
  private readonly fetcher: typeof fetch;
  private readonly getAccessToken: () => Promise<string>;
  constructor(options: { getAccessToken: () => Promise<string>; fetch?: typeof fetch }) {
    this.fetcher = options.fetch ?? fetch;
    this.getAccessToken = options.getAccessToken;
  }

  private async mapMessage(message: z.infer<typeof messageSchema>): Promise<Mail> {
    const hydrate = async (part: GmailPart, depth: number): Promise<void> => {
      if (depth > 30) throw new Error("Gmail message MIME nesting exceeds the limit");
      if (
        !part.filename &&
        (part.mimeType === "text/plain" || part.mimeType === "text/html") &&
        part.body?.data === undefined &&
        part.body?.attachmentId
      ) {
        if ((part.body.size ?? 0) > 1024 * 1024)
          throw new Error("Gmail message text exceeds the 1 MiB limit");
        const bytes = await this.getAttachment(message.id, part.body.attachmentId);
        if (bytes.length > 1024 * 1024)
          throw new Error("Gmail message text exceeds the 1 MiB limit");
        part.body.data = Buffer.from(bytes).toString("base64url");
      }
      for (const child of part.parts ?? []) await hydrate(child, depth + 1);
    };
    if (message.payload) await hydrate(message.payload, 0);
    return mapMessage(message);
  }

  async getThread(threadId: string): Promise<Mail[]> {
    const thread = z
      .object({ id: z.string(), messages: z.array(messageSchema).default([]) })
      .parse(await this.request(`${GMAIL}/threads/${idPath(threadId)}?format=full`));
    if (thread.id !== threadId || thread.messages.some((message) => message.threadId !== threadId))
      throw new Error("Google returned messages from a different thread");
    return Promise.all(thread.messages.map((message) => this.mapMessage(message)));
  }

  async listCalendars(): Promise<CalendarListEntry[]> {
    const entries: CalendarListEntry[] = [];
    const tokens = new Set<string>();
    let pageToken: string | undefined;
    do {
      const params = new URLSearchParams({ maxResults: "250" });
      if (pageToken) params.set("pageToken", pageToken);
      const result = z
        .object({
          items: z
            .array(
              z.object({
                id: z.string().min(1),
                summary: z.string().default("(Untitled calendar)"),
                summaryOverride: z.string().optional(),
                timeZone: z.string(),
                accessRole: z.string(),
              }),
            )
            .default([]),
          nextPageToken: z.string().min(1).optional(),
        })
        .parse(await this.request(`${CALENDAR}/users/me/calendarList?${params}`));
      entries.push(
        ...result.items.map((calendar) => ({
          id: calendar.id,
          name: calendar.summaryOverride ?? calendar.summary,
          timeZone: calendar.timeZone,
          accessRole: calendar.accessRole,
        })),
      );
      pageToken = result.nextPageToken;
      if (pageToken) {
        if (tokens.has(pageToken) || tokens.size >= 10)
          throw new Error("Google calendar list exceeded the pagination limit");
        tokens.add(pageToken);
      }
    } while (pageToken);
    return entries;
  }

  private async readSingleEvent(
    calendarId: string,
    eventId: string,
  ): Promise<z.infer<typeof googleEventSchema>> {
    const event = googleEventSchema.parse(
      await this.request(`${calendarPath(calendarId)}/${idPath(eventId)}`),
    );
    if (event.id !== eventId) throw new Error("Google returned a different event");
    if (event.recurrence !== undefined || event.recurringEventId !== undefined)
      throw new RecurringEventError();
    return event;
  }

  async validateSingleEvent(calendarId: string, eventId: string): Promise<void> {
    await this.readSingleEvent(calendarId, eventId);
  }

  async reviewEvent(
    calendarId: string,
    eventId: string,
  ): Promise<{ event: CalendarEvent; version: string }> {
    const current = await this.readSingleEvent(calendarId, eventId);
    const version = this.eventVersion(current);
    const timeZone =
      current.start.timeZone ??
      z
        .object({ timeZone: z.string().min(1) })
        .parse(
          await this.request(`${CALENDAR}/users/me/calendarList/${encodeURIComponent(calendarId)}`),
        ).timeZone;
    return { event: mapEvent(current, calendarId, timeZone), version };
  }

  private eventVersion(event: z.infer<typeof googleEventSchema>, expectedVersion?: string): string {
    if (!event.etag?.trim()) throw new Error("Google event has no ETag; prepare a fresh review");
    const version = singleLine(event.etag, "event ETag");
    if (expectedVersion !== undefined && version !== expectedVersion)
      throw new GoogleApiError(409, "This event changed since review. Prepare a new action.");
    return version;
  }

  private async request(
    url: string,
    method = "GET",
    body?: unknown,
    conditionalHeaders: Record<string, string> = {},
  ): Promise<unknown> {
    const write = method !== "GET";
    // Credential failures happen before dispatch, so their outcome is definite.
    const token = await this.getAccessToken();
    if (!token || /[\r\n]/.test(token))
      throw new Error("Google access token is missing or invalid; reconnect Google");
    let response: Response;
    try {
      response = await this.fetcher(url, {
        method,
        headers: {
          Authorization: `Bearer ${token}`,
          Accept: "application/json",
          ...(body === undefined ? {} : { "Content-Type": "application/json" }),
          ...conditionalHeaders,
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(30000),
        redirect: "error",
      });
    } catch {
      if (write) throw new OutcomeUnknownError();
      throw new Error("Could not reach Google; check the connection and try again");
    }
    if (write && (response.status >= 500 || response.status === 408)) {
      try {
        await response.body?.cancel();
      } catch {
        throw new OutcomeUnknownError();
      }
      throw new OutcomeUnknownError();
    }
    if (!response.ok) {
      let detail = response.statusText || "Request failed";
      try {
        const result = z
          .object({ error: z.object({ message: z.string() }) })
          .safeParse(await readJson(response));
        if (result.success) detail = result.data.error.message.slice(0, 500);
      } catch {
        /* Preserve the definite HTTP rejection even if its body is not JSON. */
      }
      throw new GoogleApiError(response.status, detail);
    }
    if (method === "DELETE" && response.status === 204) return undefined;
    try {
      return await readJson(response);
    } catch {
      if (write) throw new OutcomeUnknownError();
      throw new Error("Google returned an invalid or oversized response");
    }
  }

  /** The latest 30 matching messages. Permission/read failures propagate visibly. */
  async listMail(query = "in:inbox"): Promise<Mail[]> {
    const params = new URLSearchParams({ maxResults: "30", q: query });
    const list = z
      .object({ messages: z.array(z.object({ id: z.string() })).default([]) })
      .parse(await this.request(`${GMAIL}/messages?${params}`));
    return Promise.all(
      list.messages
        .slice(0, 30)
        .map(async ({ id }) =>
          this.mapMessage(
            messageSchema.parse(await this.request(`${GMAIL}/messages/${idPath(id)}?format=full`)),
          ),
        ),
    );
  }

  /** At most 100 occurrences in a bounded window, beginning at local midnight by default. */
  async listEvents(options: ListEventsOptions = {}): Promise<CalendarEvent[]> {
    const calendarId = options.calendarId ?? "primary";
    const path = calendarPath(calendarId);
    const midnight = new Date();
    midnight.setHours(0, 0, 0, 0);
    const timeMin = options.timeMin ?? midnight.toISOString();
    const timestamp = z.iso.datetime({ offset: true });
    if (!timestamp.safeParse(timeMin).success)
      throw new Error("Invalid calendar timeMin: use a date-time with an explicit offset");
    const timeMax =
      options.timeMax ?? new Date(Date.parse(timeMin) + 31 * 24 * 60 * 60 * 1000).toISOString();
    if (!timestamp.safeParse(timeMax).success)
      throw new Error("Invalid calendar timeMax: use a date-time with an explicit offset");
    const duration = Date.parse(timeMax) - Date.parse(timeMin);
    if (duration <= 0 || duration > 366 * 24 * 60 * 60 * 1000)
      throw new Error("Calendar range must end after it starts and span at most 366 days");
    const params = new URLSearchParams({
      maxResults: "100",
      singleEvents: "true",
      orderBy: "startTime",
      timeMin,
      timeMax,
    });
    const result = z
      .object({ items: z.array(z.unknown()).default([]), timeZone: z.string().default("UTC") })
      .parse(await this.request(`${path}?${params}`));
    return result.items.slice(0, 100).map((item) => mapEvent(item, calendarId, result.timeZone));
  }

  async getAttachment(messageId: string, attachmentId: string): Promise<Uint8Array> {
    const data = z
      .object({ data: z.string(), size: z.number().int().nonnegative().optional() })
      .parse(
        await this.request(
          `${GMAIL}/messages/${idPath(messageId)}/attachments/${idPath(attachmentId)}`,
        ),
      );
    if (data.size !== undefined && data.size > MAX_ATTACHMENT_BYTES)
      throw new Error("Attachment is too large (10 MiB limit)");
    const bytes = decodeBase64url(data.data);
    if (data.size !== undefined && data.size !== bytes.length)
      throw new Error("Google attachment size does not match its actual byte length");
    return new Uint8Array(bytes);
  }

  async sendEmail(
    input: EmailDraft,
    attachments: MailAttachment[],
  ): Promise<{ id: string; threadId?: string }> {
    const draft = emailDraftSchema.parse(input);
    singleLine(draft.subject, "subject");
    for (const address of [...draft.to, ...draft.cc, ...draft.bcc])
      singleLine(address, "recipient");
    validateAttachments(attachments);
    if (draft.threadId && !draft.replyToMessageId)
      throw new Error("Replies require replyToMessageId to resolve the source message headers");
    let threadId: string | undefined;
    const replyHeaders: string[] = [];
    if (draft.replyToMessageId) {
      const params = new URLSearchParams({ format: "metadata" });
      for (const name of ["Message-ID", "References", "Subject"])
        params.append("metadataHeaders", name);
      const source = messageSchema.parse(
        await this.request(`${GMAIL}/messages/${idPath(draft.replyToMessageId)}?${params}`),
      );
      if (draft.threadId && source.threadId !== draft.threadId)
        throw new Error("Reply thread does not match the source message");
      threadId = source.threadId;
      const metadata = headers(source.payload);
      const messageId = singleLine(metadata.get("message-id") ?? "", "Message-ID");
      if (!/^<[^<>\s]+@[^<>\s]+>$/.test(messageId))
        throw new Error("Source message has no valid Message-ID for reply threading");
      const normalizedSubject = (subject: string) =>
        decodeHeader(subject)
          .replace(/^(?:\s*re:\s*)+/i, "")
          .trim();
      if (normalizedSubject(draft.subject) !== normalizedSubject(metadata.get("subject") ?? ""))
        throw new Error("Reply subject must match the source message subject");
      const references = singleLine(metadata.get("references") ?? "", "References").trim();
      if (references && !/^(?:<[^<>\s]+@[^<>\s]+>\s*)+$/.test(references))
        throw new Error("Source message has invalid References headers");
      const chain = [...new Set([...(references.match(/<[^<>\s]+>/g) ?? []), messageId])];
      if (chain.join(" ").length > 950)
        throw new Error("Reply References header exceeds the supported length");
      replyHeaders.push(`In-Reply-To: ${messageId}`, `References: ${chain.join(" ")}`);
    }
    const profile = z
      .object({ emailAddress: z.email() })
      .parse(await this.request(`${GMAIL}/profile`));
    const mimeHeaders = [
      `From: ${singleLine(profile.emailAddress, "sender")}`,
      `To: ${draft.to.join(",\r\n ")}`,
      ...(draft.cc.length ? [`Cc: ${draft.cc.join(",\r\n ")}`] : []),
      ...(draft.bcc.length ? [`Bcc: ${draft.bcc.join(",\r\n ")}`] : []),
      `Subject: ${encodedSubject(draft.subject)}`,
      `Date: ${new Date().toUTCString()}`,
      `Message-ID: <${randomUUID()}@agent.invalid>`,
      ...replyHeaders,
      "MIME-Version: 1.0",
    ];
    const textPart = [
      "Content-Type: text/plain; charset=UTF-8",
      "Content-Transfer-Encoding: base64",
      "",
      wrapBase64(Buffer.from(draft.body.replace(/\r\n|\r|\n/g, "\r\n"))),
    ].join("\r\n");
    let mime: string;
    if (!attachments.length) mime = [...mimeHeaders, textPart].join("\r\n");
    else {
      const boundary = `agent_${randomUUID()}`;
      const parts = [
        textPart,
        ...attachments.map((attachment) => {
          const name = encodeURIComponent(attachment.name).replace(
            /['()*]/g,
            (char) => `%${char.charCodeAt(0).toString(16).toUpperCase()}`,
          );
          return [
            `Content-Type: ${attachment.mimeType}`,
            `Content-Disposition: attachment; filename*=UTF-8''${name}`,
            "Content-Transfer-Encoding: base64",
            "",
            wrapBase64(attachment.bytes),
          ].join("\r\n");
        }),
      ];
      mime = [
        ...mimeHeaders,
        `Content-Type: multipart/mixed; boundary="${boundary}"`,
        "",
        ...parts.map((part) => `--${boundary}\r\n${part}`),
        `--${boundary}--`,
        "",
      ].join("\r\n");
    }
    const result = await this.request(`${GMAIL}/messages/send`, "POST", {
      raw: Buffer.from(mime).toString("base64url"),
      ...(threadId ? { threadId } : {}),
    });
    const parsed = z
      .object({ id: z.string().min(1), threadId: z.string().optional() })
      .safeParse(result);
    if (!parsed.success) throw new OutcomeUnknownError();
    return parsed.data;
  }

  async createEvent(input: EventDraft): Promise<CalendarEvent> {
    const draft = eventDraftSchema.parse(input);
    const result = await this.request(
      `${calendarPath(draft.calendarId)}?sendUpdates=all`,
      "POST",
      eventBody(draft),
    );
    try {
      return mapEvent(result, draft.calendarId, draft.timeZone);
    } catch {
      throw new OutcomeUnknownError();
    }
  }

  async updateEvent(
    eventId: string,
    input: EventDraft,
    expectedVersion?: string,
  ): Promise<CalendarEvent> {
    const draft = eventDraftSchema.parse(input);
    const current = await this.readSingleEvent(draft.calendarId, eventId);
    const result = await this.request(
      `${calendarPath(draft.calendarId)}/${idPath(eventId)}?sendUpdates=all`,
      "PATCH",
      eventBody(draft, true),
      { "If-Match": this.eventVersion(current, expectedVersion) },
    );
    try {
      return mapEvent(result, draft.calendarId, draft.timeZone);
    } catch {
      throw new OutcomeUnknownError();
    }
  }

  async deleteEvent(calendarId: string, eventId: string, expectedVersion?: string): Promise<void> {
    const current = await this.readSingleEvent(calendarId, eventId);
    await this.request(
      `${calendarPath(calendarId)}/${idPath(eventId)}?sendUpdates=all`,
      "DELETE",
      undefined,
      { "If-Match": this.eventVersion(current, expectedVersion) },
    );
  }
}
