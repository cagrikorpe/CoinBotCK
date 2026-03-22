
var _IsActive = "/Main/IsUserControl";
var _IsIsRegistry = "/Main/IsRegistryControl";
//$(document).ready(function () {
//    var sad = document.getElementById("clsnmm").value;
//    var OU = document.getElementById(sad).;

//    console.log(sad)
//    console.log(OU)
//});

const regex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
function Say() {
    //var mailv = regex.test(document.getElementById("IsEmail").value)
    //if (mailv) {
    //    var A10 = document.getElementById("IsEmail").value.length;
    //} else {
    //    var A10 = 0;
    //}

    var A1 = document.getElementById("Username").value.length;
    var A2 = document.getElementById("IsName").value.length;
    var A3 = document.getElementById("IsSurname").value.length;
    var A4 = document.getElementById("DisplayName").value.length;
    var A5 = document.getElementById("Description").value.length;
    var A6 = document.getElementById("Office").value.length;
    var A7 = document.getElementById("Adress").value.length;
    var A8 = document.getElementById("City").value.length;
    var A9 = document.getElementById("District").value.length;
    var A10 = document.getElementById("IsEmail").value.length;
    var A11 = document.getElementById("Phone").value.length;
    var A12 = document.getElementById("Mobile").value.length;
    var A13 = document.getElementById("Title").value.length;
    var A14 = document.getElementById("department").value.length;
    var A15 = document.getElementById("moveOu").value.length;
    var A16 = document.getElementById("IsRegistry").value.length;
    if (A1 === 0 || A2 === 0 || A3 === 0 || A4 === 0 || A5 === 0 || A6 === 0 || A7 === 0 || A8 === 0 || A9 === 0 || A10 === 0 || A11 === 0 || A12 === 0 || A13 === 0 || A14 === 0 || A15 ===0 || A16 ===0 ) {
        document.getElementById("recbutton").disabled = true;
    } else {
        document.getElementById("recbutton").disabled = false;
    }
}


function Saychn() {
    //var mailv = regex.test(document.getElementById("IsEmail").value)
    //if (mailv) {
    //    var A6 = document.getElementById("IsEmail").value.length;
    //} else {
    //    var A6 = 0;
    //}

    var A1 = document.getElementById("Username").value.length;
    var A2 = document.getElementById("IsName").value.length;
    var A3 = document.getElementById("IsSurname").value.length;
    var A4 = document.getElementById("DisplayName").value.length;
    var A5 = document.getElementById("Description").value.length;
    var A6 = document.getElementById("IsEmail").value.length;
    var A7 = document.getElementById("Phone").value.length;
    var A8 = document.getElementById("Mobile").value.length;
    var A9 = document.getElementById("Title").value.length;
    var A10 = document.getElementById("IsRegistry").value.length;
    if (A1 === 0 || A2 === 0 || A3 === 0 || A4 === 0 || A5 === 0 || A6 === 0 || A7 === 0 || A8 === 0 || A9 === 0 || A10 === 0) {
        document.getElementById("recbutton").disabled = true;
    } else {
        document.getElementById("recbutton").disabled = false;
    }
}





$(document).ready(function () {
    var age = document.getElementById("clsnmm").value;
    let liElement = document.getElementById(age);
    liElement.setAttribute("data-jstree", '{ "selected": true }');
    liElement.outerHTML = liElement.outerHTML;
    //alert(age);

});


function OuMove(itm) {
    var asd = $(itm).attr("data-Ou");
    //alert(asd)
    document.getElementById("moveOu").value = asd;
    Say();
}


document.getElementById("Username").addEventListener("blur", function () {
    var asd = document.getElementById('Username').value;
    if (asd!="") {
        $.post(_IsActive + "?" + Date, { "username": asd }, function (a, b, c) {

            if (a.Success === true) {

                swal.fire({
                    title: '"' + asd + '" Kullanıcı adı kullanılmaktadır, lütfen farklı bir kullanıcı adı girişi yapınız.',
                    type: 'warning',
                    confirmButtonText: 'Tamam',
                    reverseButtons: true
                })
                document.getElementById("peer").value = "";
                document.getElementById("recbutton").disabled = true;
            } else {
                swal.fire({
                    title: '"' + asd + '" Kullanıcı adı kullanılabilir durumdadır.',
                    type: 'warning',
                    confirmButtonText: 'Tamam',
                    reverseButtons: true
                })
                //document.getElementById("recbutton").disabled = false;

                document.getElementById("peer").value = "oke";
            }
        });
    }
    removeSpaces();
});


document.getElementById("IsRegistry").addEventListener("blur", function () {
    var asd = document.getElementById('IsRegistry').value;
    if (asd!="") {
        $.post(_IsIsRegistry + "?" + Date, { "Registry": asd }, function (a, b, c) {

            if (a.Success === true) {

                swal.fire({
                    title: '"' + asd + '" numarali sicil numarası farklı bir kullanıcıda kullanılmaktadır.',
                    type: 'warning',
                    confirmButtonText: 'Tamam',
                    reverseButtons: true
                })
                document.getElementById("peer").value = "";
                document.getElementById("recbutton").disabled = true;
            } else {
                swal.fire({
                    title: '"' + asd + '" numaralı sicil numarası kullanılabilir durumdadır.',
                    type: 'warning',
                    confirmButtonText: 'Tamam',
                    reverseButtons: true
                })
                //document.getElementById("recbutton").disabled = false;

                document.getElementById("peer").value = "oke";
            }
        });
    }
    removeSpaces();
});

function ceviri() {
    var input = document.getElementById("Username");
    input.value = turkishToEnglish(input.value);
    Say();
}

function kullaniciadi() {
    var input = document.getElementById("IsName");
    input.value = turkishToEnglish(input.value);
    Say();
}

function kullanıcısoyadi() {
    var input = document.getElementById("IsSurname");
    input.value = turkishToEnglish(input.value);
    Say();
}

function emailceviri() {
    var input = document.getElementById("IsEmail");
    input.value = turkishToEnglish(input.value);
    Say();
}




function cceviri() {
    var input = document.getElementById("Username");
    input.value = turkishToEnglish(input.value);
    Saychn();
}

function ckullaniciadi() {
    var input = document.getElementById("IsName");
    input.value = turkishToEnglish(input.value);
    Saychn();
}

function ckullanıcısoyadi() {
    var input = document.getElementById("IsSurname");
    input.value = turkishToEnglish(input.value);
    Saychn();
}

function cemailceviri() {
    var input = document.getElementById("IsEmail");
    input.value = turkishToEnglish(input.value);
    Saychn();
}





function turkishToEnglish(text) {
    var turkishChars = 'şŞıİğĞüÜöÖçÇ';
    var englishChars = 'sSiIgGuUoOcC';
    var newText = '';
    for (var i = 0; i < text.length; i++) {
        var charIndex = turkishChars.indexOf(text.charAt(i));
        if (charIndex != -1) {
            newText += englishChars.charAt(charIndex);
        }
        else {
            newText += text.charAt(i);
        }
    }
    return newText;
}


function removeSpaces() {
    const input = document.getElementById("Username");
    const value = input.value.replace(/\s+/g, '');
    input.value = value;
}