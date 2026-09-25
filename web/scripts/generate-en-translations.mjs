#!/usr/bin/env node
/**
 * Generates `src/locale/messages.en.xlf` from the extracted Russian source messages.
 *
 * Russian is the source language of the application; this script keeps the English translation in
 * sync with `ng extract-i18n` output. Idiomatic names, `id`s and placeholder tags (`<x id="..."/>`)
 * are preserved verbatim, so the file is directly consumable by `ng build --localize`.
 *
 * Usage: npm run i18n:en   (after `npm run extract-i18n`)
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const sourceFile = resolve(root, 'src/locale/messages.ru.xlf');
const targetFile = resolve(root, 'src/locale/messages.en.xlf');

/** English text per message id. `[[name]]` is replaced by the matching placeholder tag. */
const TRANSLATIONS = {
  'app.name': 'File manager',
  'route.login': 'Sign in',
  'route.browse': 'Files',
  'route.history': 'History',
  'route.profile': 'Profile',
  'route.users': 'Users',
  'route.userNew': 'New user',
  'route.userEdit': 'User',

  'error.network': 'Cannot reach the server. Check your connection.',
  'error.badRequest': 'Invalid request.',
  'error.unauthorized': 'Authentication required.',
  'error.forbidden': 'You do not have permission to perform this operation.',
  'error.notFound': 'The object was not found.',
  'error.conflict': 'Conflict with the current state.',
  'error.tooLarge': 'The file is too large.',
  'error.tooManyRequests': 'Too many requests. Try again later.',
  'error.server': 'Internal server error.',

  'session.superseded':
    'Someone signed in from another device or browser. Simultaneous sessions are not allowed, so this session was closed.',
  'session.passwordChanged': 'The password was changed. Please sign in again.',
  'session.expired': 'The session has expired. Please sign in again.',
  'session.accountUnusable': 'This account can no longer sign in.',
  'session.signedOut': 'The session has ended. Please sign in again.',
  'session.generic': 'The session has ended. Please sign in again.',

  'entryType.file': 'File',
  'entryType.directory': 'Folder',
  'entryType.symlink': 'Symbolic link',
  'entryType.other': 'Special file',

  'access.readWriteExecute': 'read, write, execute',
  'access.readWrite': 'read, write',
  'access.readExecute': 'read, execute',
  'access.readOnly': 'read only',
  'access.writeExecute': 'write, execute',
  'access.writeOnly': 'write only',
  'access.executeOnly': 'execute only',
  'access.none': 'no access',
  'access.unknown': 'unknown',

  'pathAccess.read': 'Read',
  'pathAccess.write': 'Write',
  'pathAccess.readWrite': 'Read and write',

  'conflict.fail': 'Abort the operation',
  'conflict.overwrite': 'Overwrite',
  'conflict.skip': 'Skip',
  'conflict.rename': 'Rename the copy',

  'common.apply': 'Apply',
  'common.back': 'Back',
  'common.cancel': 'Cancel',
  'common.close': 'Close',
  'common.create': 'Create',
  'common.delete': 'Delete',
  'common.dismiss': 'Dismiss',
  'common.edit': 'Edit',
  'common.no': 'No',
  'common.refresh': 'Refresh',
  'common.remove': 'Remove',
  'common.rename': 'Rename',
  'common.retry': 'Retry',
  'common.save': 'Save',
  'common.yes': 'Yes',

  'upload.aborted': 'Upload cancelled.',
  'upload.cancel': 'Cancel',
  'upload.canceled': 'cancelled',
  'upload.clear': 'Clear',
  'upload.done': 'done',
  'upload.failed': 'The file could not be uploaded.',
  'upload.finished': 'Upload finished.',
  'upload.pending': 'queued',
  'upload.title': 'Uploading files',

  'browse.clipboardClear': 'Clear the clipboard',
  'browse.clipboardCopy': 'Clipboard: copy, items — [[INTERPOLATION]]',
  'browse.clipboardCut': 'Clipboard: move, items — [[INTERPOLATION]]',
  'browse.colAccess': 'Access',
  'browse.colModified': 'Modified',
  'browse.colMode': 'Mode',
  'browse.colName': 'Name',
  'browse.colOwner': 'Owner',
  'browse.colSize': 'Size',
  'browse.copy': 'Copy',
  'browse.copyDone': 'Items copied: [[count]].',
  'browse.copyFailed': 'The items could not be copied.',
  'browse.copiedToClipboard': 'Copied to the clipboard: [[count]].',
  'browse.createFailed': 'The folder could not be created.',
  'browse.created': 'Folder created.',
  'browse.crumbsAria': 'Path navigation',
  'browse.cut': 'Cut',
  'browse.cutToClipboard': 'Cut to the clipboard: [[count]].',
  'browse.delete': 'Delete',
  'browse.deleteFailed': 'delete failed',
  'browse.deleted': 'Items deleted: [[count]].',
  'browse.download': 'Download',
  'browse.dropHere': 'Drop the files to upload them into this folder',
  'browse.empty': 'The folder is empty.',
  'browse.emptyHint': 'Drag files here or use the upload buttons.',
  'browse.go': 'Go',
  'browse.loadFailed': 'The folder could not be opened.',
  'browse.loading': 'Loading the listing',
  'browse.moveDone': 'Items moved: [[count]].',
  'browse.moveFailed': 'The items could not be moved.',
  'browse.newFolder': 'New folder',
  'browse.newFolderLabel': 'Folder name',
  'browse.newFolderTitle': 'New folder',
  'browse.paste': 'Paste',
  'browse.pasteIntoSelf': 'An item cannot be pasted into itself.',
  'browse.pasteNoop': 'The items are already in this folder.',
  'browse.pathLabel': 'Path',
  'browse.properties': 'Properties',
  'browse.recent': 'Recent folders',
  'browse.recentAria': 'Recent folders',
  'browse.recentEmpty': 'The history is empty.',
  'browse.refresh': 'Refresh the listing',
  'browse.rename': 'Rename',
  'browse.renameFailed': 'The item could not be renamed.',
  'browse.renameLabel': 'New name',
  'browse.renameTitle': 'Rename',
  'browse.renamed': 'The item was renamed.',
  'browse.selectAll': 'Select all items',
  'browse.toolbarAria': 'File actions',
  'browse.transferSkipped': 'Items skipped: [[count]].',
  'browse.truncated': 'Not all items are listed: the folder contains [[total]] entries and the list is limited.',
  'browse.up': 'Go to the parent folder',
  'browse.uploadFiles': 'Upload files',
  'browse.uploadFolder': 'Upload folder',

  'delete.directoriesHint': 'Folders can be deleted recursively together with their contents and nested folders.',
  'delete.emptyOnly': 'Delete empty ones only',
  'delete.messageMany': 'Delete the selected items ([[count]])?',
  'delete.messageOne': 'Delete “[[name]]”?',
  'delete.recursive': 'Delete recursively',
  'delete.title': 'Confirm deletion',

  'conflict.message': 'The target folder already contains items with the same names. Choose an action:',
  'conflict.more': '…and more',
  'conflict.title': 'Name conflicts detected',

  'properties.access': 'Access',
  'properties.linkTarget': 'Links to',
  'properties.modified': 'Modified',
  'properties.mode': 'Permissions (mode)',
  'properties.name': 'Name',
  'properties.owner': 'Owner / group',
  'properties.path': 'Full path',
  'properties.size': 'Size',
  'properties.title': 'Properties',
  'properties.type': 'Type',

  'prompt.noSlash': 'The name cannot contain “/”.',
  'prompt.required': 'Enter a name.',
  'prompt.reserved': 'This name is not allowed.',

  'history.clear': 'Clear history',
  'history.clearConfirm': 'Clear',
  'history.clearFailed': 'The history could not be cleared.',
  'history.clearMessage': 'Delete the whole browsing history? This action cannot be undone.',
  'history.clearTitle': 'Clear history',
  'history.cleared': 'History cleared.',
  'history.colActions': 'Actions',
  'history.colCount': 'Visits',
  'history.colPath': 'Folder',
  'history.colVisited': 'Visited',
  'history.empty': 'The history is empty.',
  'history.loadFailed': 'The history could not be loaded.',
  'history.open': 'Open',
  'history.paginatorAria': 'History pagination',
  'history.title': 'Browsing history',

  'login.alreadySignedIn':
    'This account is already in use in another browser. Sign out there to sign in here.',
  'login.alreadySignedInAt':
    'This account is already in use in another browser (last activity [[moment]]). Sign out there to sign in here.',
  'login.failed': 'Sign in failed. Check the user name and password.',
  'login.hidePassword': 'Hide the password',
  'login.password': 'Password',
  'login.passwordRequired': 'Enter a password.',
  'login.submit': 'Sign in',
  'login.title': 'Sign in to the file manager',
  'login.userName': 'User name',
  'login.userNameRequired': 'Enter a user name.',
  'login.welcome': 'Welcome, [[name]]!',

  'profile.account': 'Account',
  'profile.acl': 'POSIX ACL',
  'profile.aclUnavailable':
    'POSIX ACLs are unavailable: managing folder permissions through ACLs is disabled and access lists cannot be shown.',
  'profile.admin': 'Administrator',
  'profile.database': 'Database',
  'profile.databaseUnavailable':
    'The database is unavailable: browsing history and sessions may not work correctly.',
  'profile.fullName': 'Full name',
  'profile.groups': 'Groups',
  'profile.home': 'Home folder',
  'profile.impersonation': 'User impersonation',
  'profile.noRoots': 'not configured',
  'profile.os': 'Operating system',
  'profile.pam': 'PAM',
  'profile.privileged': 'Running as root',
  'profile.roots': 'Allowed roots',
  'profile.shadow': '/etc/shadow',
  'profile.sudo': 'sudo rule',
  'profile.system': 'Server capabilities',
  'profile.title': 'Profile and system capabilities',
  'profile.uidGid': 'UID / GID',
  'profile.userName': 'User name',

  'users.aclUnavailable': 'POSIX ACLs are unavailable on the server: managing folder permissions is disabled.',
  'users.addGroup': 'Add group',
  'users.addPathGrant': 'Add path',
  'users.adminOnly': 'This section is available to administrators only.',
  'users.colActions': 'Actions',
  'users.colFlags': 'Flags',
  'users.colFullName': 'Full name',
  'users.colGroups': 'Groups',
  'users.colHome': 'Home folder',
  'users.colName': 'User',
  'users.colShell': 'Shell',
  'users.colUid': 'UID / GID',
  'users.create': 'Create user',
  'users.createFailed': 'The user could not be created.',
  'users.created': 'User created.',
  'users.defaultAcl': 'default',
  'users.deleteConfirm': 'Delete the user',
  'users.deleteFailed': 'The user could not be deleted.',
  'users.deleteMessage': 'The account “[[name]]” will be deleted permanently.',
  'users.deleteRemoveHome': 'Delete the home directory [[path]]',
  'users.deleteRevokeLegend': 'Revoke ACLs on paths',
  'users.deleteTitle': 'Delete account',
  'users.deleted': 'User deleted.',
  'users.detailsFailed': 'The user details could not be loaded.',
  'users.editTitle': 'User [[INTERPOLATION]]',
  'users.empty': 'No users found.',
  'users.fieldAccess': 'Access',
  'users.fieldCreateHome': 'Create the home directory',
  'users.fieldDefaultAcl': 'Default',
  'users.fieldFullName': 'Full name',
  'users.fieldGrantSudo': 'Allow sudo',
  'users.fieldGroups': 'User groups',
  'users.fieldHome': 'Home folder',
  'users.fieldNewGroup': 'Add a group',
  'users.fieldPassword': 'Password',
  'users.fieldPasswordConfirm': 'Repeat the password',
  'users.fieldPath': 'Path',
  'users.fieldRevokeSudo': 'Remove the existing sudo rule',
  'users.fieldShell': 'Shell',
  'users.fieldSudoNopasswd': 'sudo without a password (NOPASSWD)',
  'users.fieldUid': 'UID / GID',
  'users.fieldUserName': 'User name',
  'users.flagAdmin': 'admin',
  'users.flagNoPassword': 'no password',
  'users.flagPassword': 'password',
  'users.groupsPlaceholder': 'Select groups',
  'users.includeSystem': 'Show system users',
  'users.inspect': 'Inspect ACLs',
  'users.inspectPath': 'Path to inspect',
  'users.loadFailed': 'The user list could not be loaded.',
  'users.newTitle': 'New user',
  'users.noAcl': 'No ACL data. Enter a path and press “Inspect ACLs”.',
  'users.noAclEntries': 'There are no ACL entries for this user.',
  'users.passwordHint': 'At least [[INTERPOLATION]] characters.',
  'users.passwordKeepHint': 'Leave empty to keep the current password.',
  'users.passwordMismatch': 'The passwords do not match.',
  'users.passwordRequired': 'Enter a password.',
  'users.passwordTooShort': 'The password is shorter than [[min]] characters.',
  'users.revokeAcl': 'Revoke',
  'users.search': 'Search',
  'users.sectionAccount': 'Account',
  'users.sectionAcl': 'Existing ACLs',
  'users.sectionGroups': 'Groups',
  'users.sectionPassword': 'Password',
  'users.sectionPaths': 'Folder permissions (ACL)',
  'users.sectionSudo': 'sudo permissions',
  'users.title': 'System users',
  'users.updateFailed': 'The changes could not be saved.',
  'users.updated': 'Changes saved.',
  'users.userNamePattern':
    'Lower-case Latin letters, digits, “_”, “-” and “.” are allowed; the first character must be a letter or “_”.',
  'users.userNameRequired': 'Enter a user name.',

  'users.colSession': 'Session',
  'users.sessionActive': 'active',
  'users.sessionNone': 'none',
  'users.closeSession': 'End session',
  'users.closeSessionTitle': 'End session',
  'users.closeSessionSelf':
    'This is your current session. It will end and you will have to sign in again. Continue?',
  'users.closeSessionConfirm': 'End the active session of [[name]]? They will have to sign in again.',
  'users.closeSessionConfirmLabel': 'End session',
  'users.sessionClosed': 'Session ended.',
  'users.sessionCloseFailed': 'The session could not be ended.',

  'tabConflict.title': 'Open in another tab',
  'tabConflict.body':
    'The file manager is already in use in another tab of this browser. One session means one tab: close this tab and continue in the one where you signed in.',

  'nav.aria': 'Main navigation',
  'nav.browse': 'Files',
  'nav.home': 'Home folder',
  'nav.history': 'History',
  'nav.language': 'Interface language',
  'nav.logout': 'Sign out',
  'nav.profile': 'Profile',
  'nav.toggle': 'Show or hide the navigation',
  'nav.users': 'Users',

  'language.ru': 'Russian',
  'language.en': 'English',
};

const xml = readFileSync(sourceFile, 'utf8');
const unitPattern = /(\s*)<trans-unit id="([^"]+)"[^>]*>([\s\S]*?)<\/trans-unit>/g;
const placeholderPattern = /<x id="([^"]+)"[^>]*\/>/g;

const missing = [];
const units = [];

for (const match of xml.matchAll(unitPattern)) {
  const [, indent, id, body] = match;
  const source = /<source>([\s\S]*?)<\/source>/.exec(body)?.[1] ?? '';
  const placeholders = new Map();
  for (const placeholder of source.matchAll(placeholderPattern)) {
    placeholders.set(placeholder[1], placeholder[0]);
  }

  let target = TRANSLATIONS[id];
  if (target === undefined) {
    missing.push(id);
    target = source;
  }

  target = target.replace(/\[\[([^\]]+)\]\]/g, (whole, name) => placeholders.get(name) ?? whole);

  for (const name of placeholders.keys()) {
    if (!target.includes(`<x id="${name}"`)) {
      missing.push(`${id} (placeholder ${name} lost)`);
    }
  }

  units.push(`${indent}<trans-unit id="${id}">${body}<target state="translated">${target}</target>${indent}</trans-unit>`);
}

if (missing.length > 0) {
  process.stderr.write(`Untranslated or suspicious messages:\n  ${missing.join('\n  ')}\n`);
  process.exitCode = 1;
}

const header = `<?xml version="1.0" encoding="UTF-8" ?>
<xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
  <file source-language="ru" target-language="en" datatype="plaintext" original="ng2.template">
    <body>
`;
const footer = `    </body>
  </file>
</xliff>
`;

writeFileSync(targetFile, `${header}${units.join('\n')}\n${footer}`, 'utf8');
process.stdout.write(`Wrote ${units.length} units to ${targetFile}\n`);
