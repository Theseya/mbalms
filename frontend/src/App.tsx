import type { ReactNode } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import type { Role } from './api/types'
import { useAuth } from './auth/AuthContext'
import { Layout } from './components/Layout'
import { Loading } from './components/ui'
import { AccountPage } from './pages/AccountPage'
import { LoginPage } from './pages/LoginPage'
import { GradebookPage } from './pages/manager/GradebookPage'
import { GradesPage } from './pages/manager/GradesPage'
import { GroupsPage } from './pages/manager/GroupsPage'
import { DisciplinesPage, PeriodsPage, TeachersPage } from './pages/manager/ReferencePage'
import { SchedulePage } from './pages/manager/SchedulePage'
import { StudentsPage } from './pages/manager/StudentsPage'
import { SurveyEditPage } from './pages/manager/SurveyEditPage'
import { SurveyFromTemplatePage } from './pages/manager/SurveyFromTemplatePage'
import { SurveyResultsPage } from './pages/manager/SurveyResultsPage'
import { SurveyTemplateEditPage } from './pages/manager/SurveyTemplateEditPage'
import { SurveyTemplatesPage } from './pages/manager/SurveyTemplatesPage'
import { SurveysPage } from './pages/manager/SurveysPage'
import {
  NotificationsPage, StudentGradesPage, StudentHomePage, StudentSchedulePage, StudentSurveysPage,
} from './pages/student/StudentPages'
import { SurveyTakePage } from './pages/student/SurveyTakePage'

/** UI-level guard only; every endpoint is protected on the server. */
function RequireRole({ role, children }: { role?: Role; children: ReactNode }) {
  const { me } = useAuth()
  if (me === undefined) return <Loading />
  if (me === null) return <Navigate to="/login" replace />
  if (role && me.role !== role) return <Navigate to={me.role === 'Manager' ? '/manager/groups' : '/student'} replace />
  return <>{children}</>
}

function Home() {
  const { me } = useAuth()
  if (me === undefined) return <Loading />
  if (me === null) return <Navigate to="/login" replace />
  return <Navigate to={me.role === 'Manager' ? '/manager/groups' : '/student'} replace />
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<RequireRole><Layout /></RequireRole>}>
        <Route path="/account" element={<AccountPage />} />
        <Route path="/manager" element={<RequireRole role="Manager"><Navigate to="/manager/groups" replace /></RequireRole>} />
        <Route path="/manager/groups" element={<RequireRole role="Manager"><GroupsPage /></RequireRole>} />
        <Route path="/manager/students" element={<RequireRole role="Manager"><StudentsPage /></RequireRole>} />
        <Route path="/manager/teachers" element={<RequireRole role="Manager"><TeachersPage /></RequireRole>} />
        <Route path="/manager/disciplines" element={<RequireRole role="Manager"><DisciplinesPage /></RequireRole>} />
        <Route path="/manager/periods" element={<RequireRole role="Manager"><PeriodsPage /></RequireRole>} />
        <Route path="/manager/schedule" element={<RequireRole role="Manager"><SchedulePage /></RequireRole>} />
        <Route path="/manager/grades" element={<RequireRole role="Manager"><GradesPage /></RequireRole>} />
        <Route path="/manager/gradebook" element={<RequireRole role="Manager"><GradebookPage /></RequireRole>} />
        <Route path="/manager/surveys" element={<RequireRole role="Manager"><SurveysPage /></RequireRole>} />
        <Route path="/manager/surveys/new" element={<RequireRole role="Manager"><SurveyEditPage /></RequireRole>} />
        <Route path="/manager/surveys/from-template" element={<RequireRole role="Manager"><SurveyFromTemplatePage /></RequireRole>} />
        <Route path="/manager/surveys/:id" element={<RequireRole role="Manager"><SurveyEditPage /></RequireRole>} />
        <Route path="/manager/surveys/:id/results" element={<RequireRole role="Manager"><SurveyResultsPage /></RequireRole>} />
        <Route path="/manager/survey-templates" element={<RequireRole role="Manager"><SurveyTemplatesPage /></RequireRole>} />
        <Route path="/manager/survey-templates/new" element={<RequireRole role="Manager"><SurveyTemplateEditPage /></RequireRole>} />
        <Route path="/manager/survey-templates/:id" element={<RequireRole role="Manager"><SurveyTemplateEditPage /></RequireRole>} />
        <Route path="/student" element={<RequireRole role="Student"><StudentHomePage /></RequireRole>} />
        <Route path="/student/schedule" element={<RequireRole role="Student"><StudentSchedulePage /></RequireRole>} />
        <Route path="/student/grades" element={<RequireRole role="Student"><StudentGradesPage /></RequireRole>} />
        <Route path="/student/surveys" element={<RequireRole role="Student"><StudentSurveysPage /></RequireRole>} />
        <Route path="/student/surveys/:id" element={<RequireRole role="Student"><SurveyTakePage /></RequireRole>} />
        <Route path="/student/notifications" element={<RequireRole role="Student"><NotificationsPage /></RequireRole>} />
      </Route>
      <Route path="*" element={<Home />} />
    </Routes>
  )
}
