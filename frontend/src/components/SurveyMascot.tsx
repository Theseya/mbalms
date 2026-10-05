import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import type { StudentSurveyListItem } from '../api/types'
import { mascotState, surveyCardCaptionKey, surveyCardMood, type MascotMood } from '../lib/mascot'

const INK = '#1d2330'
const FEATHER = '#2c3a52'
const FEATHER_DARK = '#1f2a3d'
const LIGHT = '#e4e9f1'
const BEAK = '#8592a8'
const BEAK_DARK = '#5d6a80'
const GOLD = '#d9962b'

function Eyes({ mood }: { mood: MascotMood }) {
  if (mood === 'done')
    return (
      <g className="mascot-eyes" fill="none" stroke={LIGHT} strokeWidth="3.5" strokeLinecap="round">
        <path d="M40 61 Q48 52 56 61" />
        <path d="M64 61 Q72 52 80 61" />
      </g>
    )
  // Pending: looking up and aside, as if thinking about the unanswered survey.
  const [dx, dy] = mood === 'pending' ? [-2.5, -3] : [0, 0]
  return (
    <g className="mascot-eyes">
      <circle cx="48" cy="60" r="9" fill="#fff" />
      <circle cx="72" cy="60" r="9" fill="#fff" />
      <circle cx={48 + dx} cy={60 + dy} r="4" fill={INK} />
      <circle cx={72 + dx} cy={60 + dy} r="4" fill={INK} />
    </g>
  )
}

export function CrowIllustration({ mood }: { mood: MascotMood }) {
  return (
    <svg className="mascot-crow" viewBox="0 0 120 120" width="88" height="88" aria-hidden="true" focusable="false">
      <ellipse cx="60" cy="114" rx="28" ry="4" fill="#d9dee6" />
      <path d="M47 98 L40 113 L60 107 L80 113 L73 98 Z" fill={FEATHER_DARK} />
      <path d="M52 104 l-4 7 M56 105 l0 7 M64 105 l0 7 M68 104 l4 7" stroke={BEAK_DARK} strokeWidth="3" strokeLinecap="round" />
      <ellipse cx="60" cy="74" rx="32" ry="34" fill={FEATHER} />
      <path d="M30 76 Q23 93 38 102 Q40 87 35 74 Z" fill={FEATHER_DARK} />
      <path d="M90 76 Q97 93 82 102 Q80 87 85 74 Z" fill={FEATHER_DARK} />
      <ellipse cx="60" cy="90" rx="18" ry="15" fill="#3f5273" />
      <Eyes mood={mood} />
      {mood === 'pending' && (
        <g stroke={LIGHT} strokeWidth="2.5" strokeLinecap="round">
          <path d="M39 49 L53 45" />
          <path d="M81 49 L67 45" />
        </g>
      )}
      {mood === 'done' ? (
        <>
          <circle cx="39" cy="71" r="4" fill="#e9a3a3" opacity="0.85" />
          <circle cx="81" cy="71" r="4" fill="#e9a3a3" opacity="0.85" />
          <path d="M51 67 L69 67 L60 78 Z" fill={BEAK} />
          <path d="M54 80 L66 80 L60 86 Z" fill={BEAK_DARK} />
        </>
      ) : (
        <>
          <path d="M51 67 L69 67 L60 87 Z" fill={BEAK} />
          <path d="M60 68 L60 84" stroke={BEAK_DARK} strokeWidth="1.5" strokeLinecap="round" />
        </>
      )}
      <path d="M60 12 L94 23 L60 34 L26 23 Z" fill={INK} />
      <path d="M44 28 L44 36 Q60 42 76 36 L76 28 L60 34 Z" fill="#2a3242" />
      <path d="M60 23 L88 27 L88 39" fill="none" stroke={GOLD} strokeWidth="2" strokeLinecap="round" />
      <circle cx="88" cy="41" r="3" fill={GOLD} />
      {mood === 'pending' && (
        <g fill="#8a95a5">
          <circle cx="100" cy="52" r="2.5" />
          <circle cx="106" cy="44" r="3.5" />
          <circle cx="112" cy="34" r="4.5" />
        </g>
      )}
    </svg>
  )
}

export function SurveyMascot({ surveys }: { surveys: StudentSurveyListItem[] }) {
  const { t } = useTranslation()
  const state = mascotState(surveys)
  const title = state.mood === 'pending'
    ? t('studentHome.mascot.remaining', { count: state.remaining })
    : t(`studentHome.mascot.${state.mood}`)
  const action = state.mood === 'pending' && state.remaining === 1 ? t('studentHome.mascot.takeSurvey') : t('studentHome.goToSurveys')
  return (
    <Link to={state.link} className={`card stat mascot mascot-${state.mood}`} data-mood={state.mood}>
      <CrowIllustration mood={state.mood} />
      <span className="mascot-text">
        <strong className="mascot-title">{title}</strong>
        <span className="muted">{t(`studentHome.mascot.${state.mood}Hint`)}</span>
        <span className="mascot-action">{action} →</span>
      </span>
    </Link>
  )
}

/** Compact crow + caption beside each row on the student surveys list. Decorative SVG; meaning is in the caption. */
export function SurveyCardMascot({ survey }: { survey: StudentSurveyListItem }) {
  const { t } = useTranslation()
  const mood = surveyCardMood(survey)
  return (
    <div className={`survey-card-mascot mascot-${mood}`} data-mood={mood}>
      <CrowIllustration mood={mood} />
      <span className="mascot-caption">{t(surveyCardCaptionKey(survey))}</span>
    </div>
  )
}
