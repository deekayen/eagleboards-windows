// ------------------------------------------------------------------------
// eb-data.js — adapter between the browser UI and the EagleBoardScheduler
// server protocols. The server side is unchanged from the original app:
//
//   GET  <entity>-cells?cols=A,B,C        -> <rows><row id="ID"><cell>..</cell></row></rows>
//   POST <entity>-update                  -> form fields: !nativeeditor_status
//        (inserted|updated|deleted), gr_id=<ID>, plus <Column>=<value> pairs;
//        responds <data><action type="<status|invalid>" sid=".." tid=".."/></data>
//   POST board actions (/seat-board, /complete-board, ...) -> "OK." (200) or
//        plain-text error (304)
//   GET  <entity>-autofill                -> dhtmlx-combo pseudo-JSON list
//   GET  <entity>-autofill?<Field>=<v>    -> <data><Col>value</Col>...</data>
// ------------------------------------------------------------------------

// Text typed at the sign-in door, made safe to put inside a message's HTML.
function ebEscapeHtml(text) {
   return String(text == null ? "" : text)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

function ebParseXML(text) {
   return new DOMParser().parseFromString(text, "text/xml");
}

// Fetch grid rows. Returns Promise<Array<{id, [col]: value}>>.
function ebFetchRows(path, cols) {
   var url = path + "?cols=" + encodeURIComponent(cols.join(","));
   return fetch(url).then(function (r) { return r.text(); }).then(function (text) {
      var doc = ebParseXML(text);
      var out = [];
      var rows = doc.getElementsByTagName("row");
      for (var i = 0; i < rows.length; i++) {
         var rec = { id: rows[i].getAttribute("id") };
         var cells = rows[i].getElementsByTagName("cell");
         for (var c = 0; c < cells.length && c < cols.length; c++) {
            rec[cols[c]] = cells[c].textContent;
         }
         out.push(rec);
      }
      return out;
   });
}

function ebFormBody(fields) {
   var parts = [];
   for (var k in fields) {
      if (Object.prototype.hasOwnProperty.call(fields, k) && fields[k] != null) {
         parts.push(encodeURIComponent(k) + "=" + encodeURIComponent(fields[k]));
      }
   }
   return parts.join("&");
}

// Save one record edit. status: "inserted" | "updated" | "deleted".
// Returns Promise<boolean> (true when the server accepted the change).
function ebSaveRow(path, status, id, fields) {
   var body = { "!nativeeditor_status": status, gr_id: id };
   for (var k in (fields || {})) {
      body[k] = fields[k];
   }
   return fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: ebFormBody(body)
   }).then(function (r) { return r.text(); }).then(function (text) {
      var action = ebParseXML(text).getElementsByTagName("action")[0];
      return !!action && action.getAttribute("type") === status;
   });
}

// Board workflow actions (/seat-board, /complete-board, /postpone-board, ...).
// Resolves {ok: bool, text: string}; the server answers "OK." on success and
// a plain-text reason with HTTP 304 on rejection.
function ebAction(path, fields) {
   return fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: ebFormBody(fields)
   }).then(function (r) {
      return r.text().then(function (text) {
         return { ok: r.status === 200 && text.indexOf("OK") === 0, text: text };
      });
   });
}

// Autofill option list. The server emits dhtmlx-combo pseudo-JSON with
// unquoted keys, so it is extracted with a regex rather than JSON.parse.
function ebAutofillList(path) {
   return fetch(path + "?op=list").then(function (r) { return r.text(); }).then(function (text) {
      var out = [];
      var re = /value: "((?:[^"\\]|\\.)*)"/g;
      var m;
      while ((m = re.exec(text)) !== null) {
         out.push(m[1]);
      }
      return out;
   });
}

// Fetch the full stored record for an autofill lookup value (usually Email).
// Returns Promise<{[col]: value}|null>.
function ebAutofillRecord(path, field, value) {
   var url = path + "?" + encodeURIComponent(field) + "=" + encodeURIComponent(value);
   return fetch(url).then(function (r) { return r.text(); }).then(function (text) {
      var data = ebParseXML(text).getElementsByTagName("data")[0];
      if (!data) {
         return null;
      }
      var rec = {};
      var found = false;
      for (var n = data.firstElementChild; n; n = n.nextElementSibling) {
         rec[n.tagName] = n.textContent;
         found = true;
      }
      return found ? rec : null;
   });
}
