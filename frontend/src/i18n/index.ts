import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import en from './en'
import ru from './ru'

export const LANGUAGES = ['ru', 'en'] as const
export type Language = (typeof LANGUAGES)[number]

export const STORAGE_KEY = 'mbalms.lang'

export function initialLanguage(): Language {
  const stored = typeof localStorage !== 'undefined' ? localStorage.getItem(STORAGE_KEY) : null
  return stored === 'en' ? 'en' : 'ru'
}

void i18n.use(initReactI18next).init({
  resources: { ru: { translation: ru }, en: { translation: en } },
  lng: initialLanguage(),
  fallbackLng: 'ru',
  returnEmptyString: false,
  interpolation: { escapeValue: false },
})

document.documentElement.lang = i18n.language

export function setLanguage(lang: Language) {
  localStorage.setItem(STORAGE_KEY, lang)
  document.documentElement.lang = lang
  void i18n.changeLanguage(lang)
}

export function currentLanguage(): Language {
  return i18n.language === 'en' ? 'en' : 'ru'
}

export default i18n
