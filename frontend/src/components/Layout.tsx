import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { LANGUAGES, currentLanguage, setLanguage, type Language } from '../i18n'

export function LanguageSwitcher() {
  const { t, i18n } = useTranslation()
  return (
    <label className="lang-switch">
      <span className="sr-only">{t('app.language')}</span>
      <select
        value={i18n.language}
        onChange={(e) => setLanguage(e.target.value as Language)}
        aria-label={t('app.language')}
      >
        {LANGUAGES.map((lang) => (
          <option key={lang} value={lang}>{lang.toUpperCase()}</option>
        ))}
      </select>
    </label>
  )
}

const managerLinks = [
  ['/manager/groups', 'nav.groups'],
  ['/manager/students', 'nav.students'],
  ['/manager/teachers', 'nav.teachers'],
  ['/manager/disciplines', 'nav.disciplines'],
  ['/manager/periods', 'nav.periods'],
  ['/manager/schedule', 'nav.schedule'],
  ['/manager/grades', 'nav.grades'],
  ['/manager/surveys', 'nav.surveys'],
] as const

const studentLinks = [
  ['/student', 'nav.home'],
  ['/student/schedule', 'nav.schedule'],
  ['/student/grades', 'nav.grades'],
  ['/student/surveys', 'nav.surveys'],
] as const

export function Layout() {
  const { t } = useTranslation()
  const { me, logout } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const [unread, setUnread] = useState(0)
  const isStudent = me?.role === 'Student'
  const links = isStudent ? studentLinks : managerLinks

  useEffect(() => {
    if (!isStudent) return
    let active = true
    const load = () =>
      api.get<{ count: number }>('/api/notifications/unread-count')
        .then((r) => active && setUnread(r.count))
        .catch(() => undefined)
    void load()
    const timer = window.setInterval(load, 60_000)
    return () => {
      active = false
      window.clearInterval(timer)
    }
  }, [isStudent, location.pathname])

  const onLogout = async () => {
    await logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="shell" lang={currentLanguage()}>
      <a href="#main" className="skip-link">{t('app.skipToContent')}</a>
      <header className="topbar">
        <button
          type="button"
          className="btn-icon menu-toggle"
          aria-expanded={menuOpen}
          aria-controls="main-nav"
          onClick={() => setMenuOpen((o) => !o)}
        >
          <span aria-hidden="true">☰</span>
          <span className="sr-only">{t('app.menu')}</span>
        </button>
        <span className="brand">{t('app.title')}</span>
        <div className="topbar-right">
          {isStudent && (
            <NavLink to="/student/notifications" className="bell" aria-label={t('nav.notifications')}>
              <span aria-hidden="true">🔔</span>
              {unread > 0 && <span className="bell-count">{unread}</span>}
            </NavLink>
          )}
          <LanguageSwitcher />
          <NavLink to="/account" className="user-name">{me?.displayName}</NavLink>
          <button type="button" className="btn btn-small" onClick={onLogout}>{t('app.logout')}</button>
        </div>
      </header>
      <div className="body">
        <nav id="main-nav" className={`sidebar${menuOpen ? ' open' : ''}`} aria-label={t('app.menu')}
          onClick={(e) => (e.target as HTMLElement).closest('a') && setMenuOpen(false)}>
          {links.map(([to, key]) => (
            <NavLink key={to} to={to} end={to === '/student'}>{t(key)}</NavLink>
          ))}
          {isStudent && <NavLink to="/student/notifications">{t('nav.notifications')}</NavLink>}
        </nav>
        <main id="main" className="content">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
