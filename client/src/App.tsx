import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { AddRoute } from './routes/AddRoute';
import { MonitorRoute } from './routes/MonitorRoute';

/**
 * Two routes, sharing nothing but the transaction type (S5). The simulator speaks
 * HTTP only; the dashboard speaks the hub only.
 */
export function App(): JSX.Element {
  return (
    <div className="app">
      <nav className="app__nav">
        <span className="app__brand">Real-Time Financial Monitor</span>
        <NavLink to="/monitor">Monitor</NavLink>
        <NavLink to="/add">Simulator</NavLink>
      </nav>

      <main className="app__main">
        <Routes>
          <Route path="/" element={<Navigate to="/monitor" replace />} />
          <Route path="/monitor" element={<MonitorRoute />} />
          <Route path="/add" element={<AddRoute />} />
          <Route path="*" element={<Navigate to="/monitor" replace />} />
        </Routes>
      </main>
    </div>
  );
}
