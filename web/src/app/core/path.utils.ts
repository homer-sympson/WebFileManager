/**
 * Small POSIX path helpers.
 *
 * The API only ever accepts absolute POSIX paths, therefore every path that leaves this module is
 * normalized to a canonical absolute form (`/`, or `/a/b` without a trailing slash). We deliberately
 * avoid `node:path` and platform separators so behaviour is identical in every browser.
 */

/** Normalizes any user supplied path to a canonical absolute POSIX path. */
export function normalizePath(input: string): string {
  const raw = (input ?? '').trim().replace(/\\/g, '/');
  const absolute = raw.startsWith('/') ? raw : `/${raw}`;
  const segments: string[] = [];

  for (const segment of absolute.split('/')) {
    if (segment === '' || segment === '.') {
      continue;
    }

    if (segment === '..') {
      segments.pop();
      continue;
    }

    segments.push(segment);
  }

  return `/${segments.join('/')}`;
}

/** Joins a base path with a relative name (or relative path) and normalizes the result. */
export function joinPath(base: string, name: string): string {
  const normalizedBase = normalizePath(base);
  const suffix = name.replace(/^\/+/, '');
  return normalizedBase === '/' ? normalizePath(`/${suffix}`) : normalizePath(`${normalizedBase}/${suffix}`);
}

/** Parent directory, or `null` for the filesystem root. */
export function parentPath(path: string): string | null {
  const normalized = normalizePath(path);
  if (normalized === '/') {
    return null;
  }

  const index = normalized.lastIndexOf('/');
  return index <= 0 ? '/' : normalized.slice(0, index);
}

/** Last segment of a path; `/` for the root. */
export function baseName(path: string): string {
  const normalized = normalizePath(path);
  if (normalized === '/') {
    return '/';
  }

  return normalized.slice(normalized.lastIndexOf('/') + 1);
}

/** True when `candidate` is `parent` itself or lives below it. */
export function isSameOrDescendant(parent: string, candidate: string): boolean {
  const normalizedParent = normalizePath(parent);
  const normalizedCandidate = normalizePath(candidate);

  if (normalizedCandidate === normalizedParent) {
    return true;
  }

  return normalizedParent === '/'
    ? normalizedCandidate.startsWith('/')
    : normalizedCandidate.startsWith(`${normalizedParent}/`);
}

/** Decodes router path segments back into an absolute path. */
export function decodePathFromSegments(segments: readonly string[]): string {
  const decoded = segments.map((segment) => {
    try {
      return decodeURIComponent(segment);
    } catch {
      return segment;
    }
  });

  return normalizePath(decoded.join('/'));
}

/**
 * Builds the `browse` router link for an absolute path, e.g. `/browse/tmp/demo`.
 *
 * Segments are passed **decoded**: the Angular router percent-encodes each segment itself, so
 * pre-encoding would turn a space into `%2520`.
 */
export function browseLink(path: string): readonly string[] {
  const segments = normalizePath(path)
    .split('/')
    .filter((segment) => segment.length > 0);

  return segments.length === 0 ? ['/browse'] : ['/browse', ...segments];
}

export interface Breadcrumb {
  readonly name: string;
  readonly path: string;
}

/** Breadcrumb chain from the root down to `path`; the first crumb is always `/`. */
export function breadcrumbsFor(path: string): readonly Breadcrumb[] {
  const normalized = normalizePath(path);
  const crumbs: Breadcrumb[] = [{ name: '/', path: '/' }];
  let current = '';

  for (const segment of normalized.split('/')) {
    if (segment.length === 0) {
      continue;
    }

    current += `/${segment}`;
    crumbs.push({ name: segment, path: current });
  }

  return crumbs;
}

/** Parses the path carried by a `browse` URL such as `/browse/tmp/demo`. */
export function pathFromBrowseUrl(url: string): string {
  const withoutQuery = url.split('?')[0]?.split('#')[0] ?? '';
  const segments = withoutQuery.split('/').filter((segment) => segment.length > 0);

  if (segments.length > 0 && segments[0] === 'browse') {
    segments.shift();
  }

  return decodePathFromSegments(segments);
}
