import { ConflictPolicy, FileEntryType, PathAccess } from './models/api.models';

/** Localized label for a file entry type. */
export function entryTypeLabel(type: FileEntryType): string {
  switch (type) {
    case 'file':
      return $localize`:@@entryType.file:Файл`;
    case 'directory':
      return $localize`:@@entryType.directory:Каталог`;
    case 'symlink':
      return $localize`:@@entryType.symlink:Символическая ссылка`;
    case 'other':
      return $localize`:@@entryType.other:Особый файл`;
  }
}

/** Localized legend for a three character access string such as `rwx`. */
export function effectiveAccessLabel(access: string): string {
  switch (access) {
    case 'rwx':
      return $localize`:@@access.readWriteExecute:чтение, запись, выполнение`;
    case 'rw-':
      return $localize`:@@access.readWrite:чтение, запись`;
    case 'r-x':
      return $localize`:@@access.readExecute:чтение, выполнение`;
    case 'r--':
      return $localize`:@@access.readOnly:только чтение`;
    case '-wx':
      return $localize`:@@access.writeExecute:запись, выполнение`;
    case '-w-':
      return $localize`:@@access.writeOnly:только запись`;
    case '--x':
      return $localize`:@@access.executeOnly:только выполнение`;
    case '---':
      return $localize`:@@access.none:нет доступа`;
    default:
      return $localize`:@@access.unknown:неизвестно`;
  }
}

/** Localized label for an ACL access level. */
export function pathAccessLabel(access: PathAccess): string {
  switch (access) {
    case 'read':
      return $localize`:@@pathAccess.read:Чтение`;
    case 'write':
      return $localize`:@@pathAccess.write:Запись`;
    case 'readWrite':
      return $localize`:@@pathAccess.readWrite:Чтение и запись`;
  }
}

/** Localized label for a copy/move conflict policy. */
export function conflictLabel(policy: ConflictPolicy): string {
  switch (policy) {
    case 'fail':
      return $localize`:@@conflict.fail:Прервать операцию`;
    case 'overwrite':
      return $localize`:@@conflict.overwrite:Перезаписать`;
    case 'skip':
      return $localize`:@@conflict.skip:Пропустить`;
    case 'rename':
      return $localize`:@@conflict.rename:Переименовать копию`;
  }
}
