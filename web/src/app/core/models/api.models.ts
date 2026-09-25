/**
 * Data transfer objects exposed by the FileManager API.
 *
 * The API serializes camelCase properties and camelCase enum names
 * (see `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` on the server), so the
 * literal unions below mirror the server enum member names exactly.
 */

export type FileEntryType = 'file' | 'directory' | 'symlink' | 'other';

export type ConflictPolicy = 'fail' | 'overwrite' | 'skip' | 'rename';

export type PathAccess = 'read' | 'write' | 'readWrite';

/** A single directory entry as returned by `GET /api/fs/list`. */
export interface FileEntry {
  readonly name: string;
  readonly fullPath: string;
  readonly type: FileEntryType;
  readonly size: number;
  /** ISO-8601 timestamp with offset. */
  readonly lastWriteTimeUtc: string;
  /** `ls -l` style mode string, e.g. `-rw-r--r--`. */
  readonly mode: string;
  readonly owner: string;
  readonly group: string;
  readonly linkTarget: string | null;
  /** Three character access string for the signed in user, e.g. `rwx`, `r-x`, `?`. */
  readonly effectiveAccess: string;
}

/** Response of `GET /api/fs/list`. */
export interface DirectoryListing {
  readonly path: string;
  readonly parent: string | null;
  readonly entries: readonly FileEntry[];
  readonly truncated: boolean;
  readonly total: number;
}

/** The signed in account. */
export interface UserProfile {
  readonly userName: string;
  readonly uid: number;
  readonly gid: number;
  readonly fullName: string;
  readonly homeDirectory: string;
  readonly isAdmin: boolean;
  readonly hasSudoRule: boolean;
  readonly groups: readonly string[];
}

export interface LoginRequest {
  readonly userName: string;
  readonly password: string;
}

export interface LoginOptions {
  readonly exposeUserList: boolean;
  readonly users: readonly string[];
}

export interface Capabilities {
  readonly os: string;
  readonly isPrivileged: boolean;
  readonly impersonationEnabled: boolean;
  readonly pamAvailable: boolean;
  readonly shadowAvailable: boolean;
  readonly aclAvailable: boolean;
  readonly databaseReady: boolean;
  readonly browseRoots: readonly string[];
}

export interface TransferRequest {
  readonly sources: readonly string[];
  readonly destination: string;
  readonly conflict: ConflictPolicy;
}

export interface TransferResult {
  readonly affected: readonly string[];
  readonly skipped: readonly string[];
}

export interface UploadResult {
  readonly path: string;
  readonly size: number;
}

export interface HistoryItem {
  readonly path: string;
  /** ISO-8601 timestamp. */
  readonly visitedUtc: string;
  readonly visitCount: number;
}

export interface PathGrantRequest {
  readonly path: string;
  readonly access: PathAccess;
  readonly default: boolean;
}

export interface AclEntry {
  readonly userName: string;
  readonly permissions: string;
  readonly isDefault: boolean;
}

export interface PathAccessInfo {
  readonly path: string;
  readonly entries: readonly AclEntry[];
  readonly error: string | null;
}

/** A host account as listed by `GET /api/users`. */
export interface HostUser {
  readonly userName: string;
  readonly uid: number;
  readonly gid: number;
  readonly fullName: string;
  readonly homeDirectory: string;
  readonly shell: string;
  readonly groups: readonly string[];
  readonly isAdmin: boolean;
  readonly hasPassword: boolean;
}

export interface HostUserList {
  readonly users: readonly HostUser[];
  readonly aclAvailable: boolean;
}

/** Full details from `GET /api/users/{name}`. */
export interface HostUserDetails {
  readonly name: string;
  readonly uid: number;
  readonly gid: number;
  readonly fullName: string;
  readonly homeDirectory: string;
  readonly shell: string;
  readonly groups: readonly string[];
  readonly isAdmin: boolean;
  readonly hasUsablePassword: boolean;
  readonly hasSudoRule: boolean;
  readonly sudoRule: string | null;
  readonly access: readonly PathAccessInfo[];
  readonly sudoersPath: string | null;
}

export interface CreateUserRequest {
  readonly userName: string;
  readonly password: string;
  readonly fullName: string | null;
  readonly shell: string | null;
  readonly createHome: boolean;
  readonly homeDirectory: string | null;
  readonly groups: readonly string[];
  readonly grantSudo: boolean;
  readonly sudoNopasswd: boolean;
  readonly pathGrants: readonly PathGrantRequest[];
}

export interface UpdateUserRequest {
  readonly fullName?: string | null;
  readonly shell?: string | null;
  readonly password?: string | null;
  readonly groups?: readonly string[];
  readonly grantSudo?: boolean;
  readonly sudoNopasswd?: boolean;
  readonly pathGrants?: readonly PathGrantRequest[];
  readonly revokeAclPaths?: readonly string[];
}

/** RFC 9457 problem document returned for every error. */
export interface ProblemDetails {
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
}
