import { randomUUID } from "node:crypto";
import { createWriteStream } from "node:fs";
import { mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { Transform } from "node:stream";
import { pipeline } from "node:stream/promises";
import type { Download } from "playwright";

export const MAX_DOWNLOAD_BYTES = 10 * 1024 * 1024;
export interface PdfDownload {
  id: string;
  name: string;
  size: number;
  mimeType: "application/pdf";
}
export interface DownloadFailure {
  id: string;
  name: string;
  code: string;
  message: string;
  createdAt: string;
}
type Outcome = DownloadFailure & { status: "pending" | "failed" };

async function persist(path: string, value: unknown) {
  await writeFile(`${path}.tmp`, JSON.stringify(value), { mode: 0o600 });
  await rename(`${path}.tmp`, path);
}

export async function readDownloadFailures(directory: string, recoverInterrupted = false) {
  const folder = join(directory, "download-outcomes");
  await mkdir(folder, { recursive: true, mode: 0o700 });
  const failures: DownloadFailure[] = [];
  for (const name of await readdir(folder)) {
    if (!name.endsWith(".json")) continue;
    const path = join(folder, name);
    const outcome = JSON.parse(await readFile(path, "utf8")) as Outcome;
    if (outcome.status === "pending" && recoverInterrupted) {
      if (await stat(join(directory, "downloads", `${outcome.id}.json`)).catch(() => null)) {
        await rm(path, { force: true });
        continue;
      }
      outcome.status = "failed";
      await persist(path, outcome);
    }
    if (outcome.status === "failed") {
      const { status: _status, ...failure } = outcome;
      failures.push(failure);
    }
  }
  return failures.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

export async function capturePdfDownload(options: {
  directory: string;
  tempDirectory: string;
  limitReached: boolean;
  download: Pick<Download, "suggestedFilename" | "createReadStream" | "cancel" | "delete">;
}) {
  const { directory, tempDirectory, download, limitReached } = options;
  const folder = join(directory, "downloads");
  const outcomes = join(directory, "download-outcomes");
  await mkdir(folder, { recursive: true, mode: 0o700 });
  await mkdir(outcomes, { recursive: true, mode: 0o700 });
  const id = randomUUID();
  const name =
    download
      .suggestedFilename()
      .replace(/[^a-zA-Z0-9._ -]/g, "_")
      .slice(0, 180) || "download";
  const outcome: Outcome = {
    id,
    name,
    status: "pending",
    code: "DOWNLOAD_INTERRUPTED",
    message: "The download was interrupted or could not be retrieved. Try downloading it again.",
    createdAt: new Date().toISOString(),
  };
  const outcomePath = join(outcomes, `${id}.json`);
  const destination = join(folder, `${id}.pdf`);
  // A worker restart can now report an interrupted transfer even if Chromium
  // never reaches its completion callback.
  await persist(outcomePath, outcome);
  let monitor: ReturnType<typeof setInterval> | undefined;
  let oversized = false;
  let published = false;
  try {
    if (limitReached) {
      outcome.code = "DOWNLOAD_LIMIT";
      outcome.message = "This session has reached its 20 PDF download limit.";
      await download.cancel();
      throw new Error("Download limit reached");
    }
    monitor = setInterval(() => {
      void readdir(tempDirectory)
        .then(async (files) => {
          for (const file of files) {
            const info = await stat(join(tempDirectory, file)).catch(() => null);
            if (info?.isFile() && info.size > MAX_DOWNLOAD_BYTES) {
              oversized = true;
              await download.cancel();
            }
          }
        })
        .catch(() => {});
    }, 100);
    const source = await download.createReadStream();
    if (!source) throw new Error("Download unavailable");
    let bytes = 0;
    let prefix = Buffer.alloc(0);
    await pipeline(
      source,
      new Transform({
        transform(chunk: Buffer, _encoding, done) {
          bytes += chunk.length;
          if (prefix.length < 5)
            prefix = Buffer.concat([prefix, chunk.subarray(0, 5 - prefix.length)]);
          if (bytes > MAX_DOWNLOAD_BYTES) {
            oversized = true;
            done(new Error("Download too large"));
          } else done(null, chunk);
        },
      }),
      createWriteStream(destination, { mode: 0o600, flags: "wx" }),
    );
    if (prefix.toString("ascii") !== "%PDF-") {
      outcome.code = "UNSUPPORTED_DOWNLOAD";
      outcome.message = "Only PDF downloads can be imported. This file was not a PDF.";
      throw new Error("Unsupported download");
    }
    const metadata: PdfDownload = {
      id,
      name: name.toLowerCase().endsWith(".pdf") ? name : `${name}.pdf`,
      size: bytes,
      mimeType: "application/pdf",
    };
    await persist(join(folder, `${id}.json`), metadata);
    published = true;
    await rm(outcomePath, { force: true });
  } catch (error) {
    // Once metadata is visible, its PDF must remain available even if clearing
    // the transfer journal fails. Restart recovery reconciles that journal.
    if (published) throw error;
    if (oversized) {
      outcome.code = "DOWNLOAD_TOO_LARGE";
      outcome.message = "The download exceeds 10 MiB. Choose a smaller PDF.";
    }
    outcome.status = "failed";
    await persist(outcomePath, outcome);
    await rm(destination, { force: true });
    // Keep a bounded recent failure history alongside the bounded PDF store.
    for (const stale of (await readDownloadFailures(directory)).slice(100)) {
      await rm(join(outcomes, `${stale.id}.json`), { force: true });
    }
  } finally {
    clearInterval(monitor);
    await download.delete().catch(() => {});
  }
}
