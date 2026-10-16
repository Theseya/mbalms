import type { Me, Role } from '../api/types'

export function meRoles(me: Me): Role[] {
  return me.roles?.length ? me.roles : [me.role]
}

export function hasRole(me: Me, role: Role): boolean {
  return meRoles(me).includes(role)
}

export function hasStaffAdmin(me: Me): boolean {
  return hasRole(me, 'Owner') || hasRole(me, 'AccountAdmin')
}

export function homePath(me: Me): string {
  if (hasStaffAdmin(me)) return '/admin/staff'
  if (hasRole(me, 'Manager')) return '/manager/groups'
  return '/student'
}
