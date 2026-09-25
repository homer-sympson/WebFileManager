import { UploadItem } from '../../core/services/upload.store';

/** Builds upload items from a plain `<input type="file" multiple>` selection. */
export function itemsFromFileList(files: FileList, useRelativePath: boolean): readonly UploadItem[] {
  return Array.from(files).map((file) => ({
    file,
    relativePath: useRelativePath ? readRelativePath(file) : null,
  }));
}

/**
 * Builds upload items from a drop event.
 *
 * When the browser exposes the `webkitGetAsEntry` API (Chromium, Safari, Firefox 50+) dropped
 * directories are walked recursively so a whole tree can be uploaded. Otherwise the flat file list
 * is used.
 */
export async function itemsFromDataTransfer(dataTransfer: DataTransfer): Promise<readonly UploadItem[]> {
  const entries = Array.from(dataTransfer.items)
    .filter((item) => item.kind === 'file')
    .map((item) => item.webkitGetAsEntry())
    .filter((entry): entry is FileSystemEntry => entry !== null);

  if (entries.length === 0) {
    return Array.from(dataTransfer.files).map((file) => ({ file, relativePath: null }));
  }

  const collected: UploadItem[] = [];
  for (const entry of entries) {
    await walkEntry(entry, '', collected);
  }

  return collected;
}

/** `webkitRelativePath` is non standard, so it is read defensively. */
export function readRelativePath(file: File): string | null {
  const candidate = (file as File & { readonly webkitRelativePath?: string }).webkitRelativePath;
  return typeof candidate === 'string' && candidate.length > 0 ? candidate : null;
}

async function walkEntry(entry: FileSystemEntry, prefix: string, collected: UploadItem[]): Promise<void> {
  const path = `${prefix}${entry.name}`;

  if (entry.isFile) {
    const file = await readFileEntry(entry as FileSystemFileEntry);
    collected.push({ file, relativePath: path });
    return;
  }

  if (entry.isDirectory) {
    const directory = entry as FileSystemDirectoryEntry;
    for (const child of await readAllEntries(directory.createReader())) {
      await walkEntry(child, `${path}/`, collected);
    }
  }
}

function readFileEntry(entry: FileSystemFileEntry): Promise<File> {
  return new Promise<File>((resolve, reject) => {
    entry.file(resolve, reject);
  });
}

/** `readEntries` returns at most 100 entries per call, so it has to be drained in a loop. */
function readAllEntries(reader: FileSystemDirectoryReader): Promise<readonly FileSystemEntry[]> {
  return new Promise<readonly FileSystemEntry[]>((resolve, reject) => {
    const all: FileSystemEntry[] = [];

    const readBatch = (): void => {
      reader.readEntries((batch) => {
        if (batch.length === 0) {
          resolve(all);
          return;
        }

        all.push(...batch);
        readBatch();
      }, reject);
    };

    readBatch();
  });
}
