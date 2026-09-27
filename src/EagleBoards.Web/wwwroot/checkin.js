// ------------------------------------------------------------------------
// checkin.js -- what the check-in pages ask the Eagle Boards app, and how
// they behave. Shared by every version; see README.md in this folder.
//
//   GET  /api/checked-in       -> { refreshSeconds, youth: [{ time, last, first, unitType, unit }],
//                                   adults: [{ last, first, unitType, unit }] }
//                                 names and units only, in sign-in order;
//                                 refreshSeconds is no longer read by these pages
//   GET  /api/scout-choices    -> [{ id, first, last, unitType, unit }], the youth
//                                 an adult may say they came to support
//   POST /api/youth-lookup     email=... -> the pre-registration it matches
//                                 (ID, Last, First, UnitType, Unit, BoardType,
//                                 Leader), or {}
//   POST /api/adult-lookup     email=... -> the adult history it matches
//                                 (ID, Last, First, Phone, UnitType, Unit,
//                                 FinalBoard, ProjectReview), or {}
//   POST /register-youth       form fields -> 200 on success, else the reason
//   POST /register-adult       form fields -> 200 on success, else the reason
//
// No birthdate is asked for, looked up or sent (SPEC.md D-7), and no youth
// phone number (D-8).
//
// Accessibility (WCAG 2.2 AA) lives here as much as in the markup: errors
// are named in words beside their field and in a summary (3.3.1, 3.3.3),
// progress and results are announced (4.1.3), the page never leaves by
// itself without a way to stop it (2.2.1), and nothing on it changes on its
// own while someone is reading it (2.2.2).
// ------------------------------------------------------------------------

function ebFormBody(fields) {
   var parts = [];
   for (var name in fields) {
      if (Object.prototype.hasOwnProperty.call(fields, name) && fields[name] != null) {
         parts.push(encodeURIComponent(name) + "=" + encodeURIComponent(fields[name]));
      }
   }
   return parts.join("&");
}

// POST a form. Resolves { ok, text }.
function ebPostForm(path, fields) {
   return fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: ebFormBody(fields)
   }).then(function (response) {
      return response.text().then(function (text) {
         return { ok: response.status === 200, text: text };
      });
   });
}

// The record an email matches, or null. A POST, so the address never lands
// in a URL on a shared tablet.
function ebLookup(path, email) {
   return fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: ebFormBody({ email: email })
   }).then(function (response) {
      return response.ok ? response.json() : {};
   }).then(function (record) {
      return record && Object.keys(record).length > 0 ? record : null;
   }).catch(function () {
      return null;
   });
}

function ebGetJson(path) {
   return fetch(path, { cache: "no-store" }).then(function (response) {
      if (!response.ok) {
         throw new Error("HTTP " + response.status);
      }
      return response.json();
   });
}

function ebUnitText(unitType, unit) {
   return ((unitType || "") + " " + (unit || "")).trim();
}

// ------------------------------------------------------------ welcome page
// The two "who has signed in" lists, loaded when the welcome page opens,
// which it does after every sign-in. They do not update on their own while
// someone is reading them, so there is nothing to pause (WCAG 2.2.2), and
// newest first, so nothing needs to scroll. A page brought back from the
// browser's history is re-read, so it never shows an old list.
function ebWelcomeLists() {
   function fill(tableId, countId, rows, cells) {
      var table = document.getElementById(tableId);
      var tbody = table.tBodies[0];
      tbody.textContent = "";
      rows.slice().reverse().forEach(function (record) {
         var tr = document.createElement("tr");
         cells(record).forEach(function (cell) {
            var td = document.createElement("td");
            td.textContent = cell.text;
            if (cell.className) {
               td.className = cell.className;
            }
            tr.appendChild(td);
         });
         tbody.appendChild(tr);
      });
      document.getElementById(countId).textContent = String(rows.length);
      table.hidden = rows.length === 0;
      table.nextElementSibling.hidden = rows.length !== 0;
   }

   function load() {
      ebGetJson("/api/checked-in").then(function (lists) {
         fill("youthTable", "youthCount", lists.youth || [], function (y) {
            return [
               { text: y.time, className: "time" },
               { text: (y.first + " " + y.last).trim() },
               { text: ebUnitText(y.unitType, y.unit) }
            ];
         });
         fill("adultTable", "adultCount", lists.adults || [], function (a) {
            return [
               { text: (a.first + " " + a.last).trim() },
               { text: ebUnitText(a.unitType, a.unit) }
            ];
         });
      }).catch(function () {
         // This screen faces the people signing in; a failed read must never
         // put an error in front of them. The lists just stay as they were.
      });
   }

   window.addEventListener("pageshow", function (event) {
      if (event.persisted) {
         load();
      }
   });
   load();
}

// ------------------------------------------------------------ sign-in forms
// Wire a sign-in form. options:
//   registerPath   "/register-youth" or "/register-adult"
//   lookupPath     "/api/youth-lookup" or "/api/adult-lookup"
//   prefill        the fields a matched email fills in
//   send           the fields posted
//   check(values)  optional; returns [{ field, message }] for rules beyond
//                  required and pattern
//   prepare(values) optional; adds to what is posted
//   afterPrefill() optional; runs once fields have been filled in
function ebSignInForm(form, options) {
   var formStatus = document.getElementById("formStatus");
   var errorSummary = document.getElementById("errorSummary");
   var submitButton = document.getElementById("submitButton");
   var done = document.getElementById("done");
   var dirty = false;

   form.noValidate = true;   // our own messages, in words, beside each field

   form.addEventListener("input", function () { dirty = true; });

   function say(text, kind) {
      formStatus.textContent = text;
      formStatus.className = "status" + (kind ? " " + kind : "");
   }

   function labelOf(control) {
      var label = form.querySelector("label[for='" + control.id + "']");
      var text = label ? label.firstChild.textContent : control.name;
      return text.trim().replace(/:$/, "");
   }

   // ---- errors (3.3.1, 3.3.3) ----
   function clearError(control) {
      control.removeAttribute("aria-invalid");
      var old = document.getElementById(control.id + "-error");
      if (old) {
         old.remove();
      }
      var described = (control.getAttribute("aria-describedby") || "").split(" ").filter(function (id) {
         return id && id !== control.id + "-error";
      });
      if (described.length) {
         control.setAttribute("aria-describedby", described.join(" "));
      } else {
         control.removeAttribute("aria-describedby");
      }
   }

   function showError(control, message) {
      clearError(control);
      control.setAttribute("aria-invalid", "true");
      var p = document.createElement("p");
      p.id = control.id + "-error";
      p.className = "field-error";
      p.textContent = message;
      control.insertAdjacentElement("afterend", p);
      var described = (control.getAttribute("aria-describedby") || "").split(" ").filter(Boolean);
      described.push(p.id);
      control.setAttribute("aria-describedby", described.join(" "));
   }

   function problems() {
      var found = [];
      Array.prototype.forEach.call(form.elements, function (control) {
         if (!control.name || control.disabled || control.type === "hidden" || !control.willValidate) {
            return;
         }
         var value = control.value.trim();
         if (control.required && value === "") {
            found.push({ field: control.name, message: control.getAttribute("data-required") || ("Enter your " + labelOf(control).toLowerCase() + ".") });
         } else if (value !== "" && control.pattern && !new RegExp("^(?:" + control.pattern + ")$").test(value)) {
            found.push({ field: control.name, message: control.getAttribute("data-pattern") || (labelOf(control) + " is not in the right form.") });
         }
      });
      if (options.check) {
         found = found.concat(options.check(values()));
      }
      return found;
   }

   function values() {
      var out = {};
      options.send.forEach(function (name) {
         var control = form.elements[name];
         out[name] = control && !control.disabled ? control.value.trim() : "";
      });
      return out;
   }

   function showProblems(list) {
      Array.prototype.forEach.call(form.querySelectorAll("[aria-invalid]"), clearError);
      errorSummary.textContent = "";
      if (list.length === 0) {
         return;
      }
      var heading = document.createElement("p");
      heading.textContent = list.length === 1 ? "One thing needs fixing:" : list.length + " things need fixing:";
      var ul = document.createElement("ul");
      list.forEach(function (problem) {
         var control = form.elements[problem.field];
         showError(control, problem.message);
         var li = document.createElement("li");
         var a = document.createElement("a");
         a.href = "#" + control.id;
         a.textContent = problem.message;
         a.addEventListener("click", function (event) {
            event.preventDefault();
            control.focus();
         });
         li.appendChild(a);
         ul.appendChild(li);
      });
      errorSummary.appendChild(heading);
      errorSummary.appendChild(ul);
      form.elements[list[0].field].focus();
   }

   // A field that was wrong is re-checked as it is corrected.
   form.addEventListener("input", function (event) {
      var control = event.target;
      if (control.getAttribute("aria-invalid") === "true") {
         var still = problems().filter(function (p) { return p.field === control.name; });
         if (still.length === 0) {
            clearError(control);
         }
      }
   });

   // ---- pre-fill from a known email (3.3.7) ----
   var emailInput = form.elements.Email;
   var lastLookedUp = "";
   var typingTimer = null;

   function lookUp() {
      var email = emailInput.value.trim();
      if (!email || email === lastLookedUp || email.toLowerCase() === "none") {
         return;
      }
      lastLookedUp = email;
      form.elements.ID.value = "";
      ebLookup(options.lookupPath, email).then(function (record) {
         if (!record || emailInput.value.trim() !== email) {
            return;
         }
         options.prefill.forEach(function (name) {
            if (record[name] != null && form.elements[name]) {
               form.elements[name].value = record[name];
            }
         });
         if (options.afterPrefill) {
            options.afterPrefill();
         }
         say("We found you and filled in what we had. Check it, and change anything that's wrong.", "");
      });
   }

   emailInput.addEventListener("change", lookUp);
   emailInput.addEventListener("input", function () {
      clearTimeout(typingTimer);
      typingTimer = setTimeout(lookUp, 700);
   });

   // ---- sending ----
   form.addEventListener("submit", function (event) {
      event.preventDefault();
      var list = problems();
      showProblems(list);
      if (list.length > 0) {
         say("", "");
         return;
      }
      var fields = values();
      if (options.prepare) {
         options.prepare(fields);
      }
      submitButton.disabled = true;
      say("Signing you in…", "");
      ebPostForm(options.registerPath, fields).then(function (result) {
         if (result.ok) {
            finish();
         } else {
            submitButton.disabled = false;
            say(result.text || "That didn't go through. Please try again, or ask someone at the table.", "error");
         }
      }).catch(function () {
         submitButton.disabled = false;
         say("That didn't go through. Please try again, or ask someone at the table.", "error");
      });
   });

   // ---- done (2.2.1: the page returns to the start by itself, after long
   // enough to read, and can be told to stay) ----
   var RETURN_SECONDS = 20;
   var returnTimer = null;

   function finish() {
      dirty = false;
      form.hidden = true;
      say("", "");
      var lead = document.querySelector(".lead");
      if (lead) {
         lead.hidden = true;
      }
      done.hidden = false;
      var heading = done.querySelector("h2");
      heading.setAttribute("tabindex", "-1");
      heading.focus();
      returnTimer = setTimeout(function () { window.location.href = "/"; }, RETURN_SECONDS * 1000);
      document.getElementById("returnNote").textContent =
         "This page goes back to the start in " + RETURN_SECONDS + " seconds.";
   }

   document.getElementById("stayButton").addEventListener("click", function () {
      clearTimeout(returnTimer);
      this.hidden = true;
      document.getElementById("returnNote").textContent = "This page will stay until you choose Back to the start.";
      // The button pressed is gone; keep keyboard focus on what comes next.
      done.querySelector("a.button").focus();
   });

   // ---- leaving (asks only when something has been typed) ----
   var leaveDialog = document.getElementById("leaveDialog");
   document.getElementById("cancelButton").addEventListener("click", function () {
      if (!dirty) {
         window.location.href = "/";
         return;
      }
      leaveDialog.showModal();
      document.getElementById("keepButton").focus();
   });
   document.getElementById("keepButton").addEventListener("click", function () { leaveDialog.close(); });
   document.getElementById("leaveButton").addEventListener("click", function () { window.location.href = "/"; });
}

// Someone serving from a district, council or community role has no unit
// number: the box is cleared, disabled and no longer required, and says why.
function ebUnitlessTypes(form, unitlessTypes) {
   var unit = form.elements.Unit;
   var hint = document.getElementById("Unit-hint");
   var normalHint = hint.textContent;
   function sync() {
      var unitless = unitlessTypes.indexOf(form.elements.UnitType.value) >= 0;
      if (unitless) {
         unit.value = "";
         unit.removeAttribute("aria-invalid");
         var error = document.getElementById("Unit-error");
         if (error) {
            error.remove();
         }
      }
      unit.disabled = unitless;
      unit.required = !unitless;
      hint.textContent = unitless ? "Not needed for a " + form.elements.UnitType.value.toLowerCase() + " role." : normalHint;
   }
   form.elements.UnitType.addEventListener("change", sync);
   sync();
   return sync;
}

// The adult form's "I'm here supporting" list, with a box to narrow it. A
// checked youth stays in view, so narrowing never hides a choice.
function ebSupportList(form) {
   var list = document.getElementById("supportList");
   var find = document.getElementById("supportFind");
   var count = document.getElementById("supportCount");

   function showCount() {
      var shown = list.querySelectorAll(".check:not([hidden])").length;
      count.textContent = shown === 1 ? "1 youth shown." : shown + " youth shown.";
   }

   ebGetJson("/api/scout-choices").then(function (scouts) {
      list.textContent = "";
      if (scouts.length === 0) {
         list.innerHTML = "<p class='empty'>No youth have signed up yet.</p>";
         find.disabled = true;
         return;
      }
      scouts.forEach(function (s, index) {
         var label = document.createElement("label");
         label.className = "check";
         var box = document.createElement("input");
         box.type = "checkbox";
         box.name = "SupportingChoice";
         box.value = s.id;
         box.id = "support-" + index;
         label.appendChild(box);
         var unit = ebUnitText(s.unitType, s.unit);
         label.appendChild(document.createTextNode((s.first + " " + s.last).trim() + (unit ? " (" + unit + ")" : "")));
         list.appendChild(label);
      });
      showCount();
   }).catch(function () {
      list.innerHTML = "<p class='empty'>The list of youth couldn't be loaded. You can still sign in.</p>";
      find.disabled = true;
   });

   find.addEventListener("input", function () {
      var needle = find.value.trim().toLowerCase();
      Array.prototype.forEach.call(list.querySelectorAll(".check"), function (label) {
         var box = label.querySelector("input");
         label.hidden = !(!needle || box.checked || label.textContent.toLowerCase().indexOf(needle) >= 0);
      });
      showCount();
   });

   return function supportingIds() {
      return Array.prototype.map.call(list.querySelectorAll("input:checked"), function (box) { return box.value; });
   };
}
