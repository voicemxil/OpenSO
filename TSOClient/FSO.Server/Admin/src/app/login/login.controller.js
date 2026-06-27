'use strict';

angular.module('admin')
  .controller('LoginCtrl', function ($scope, Auth, $location) {
      $scope.apiUrl = "https://api.openso.org";
      $scope.username = "";
      $scope.password = "";

      $scope.loginLocked = false;

      $scope.login = function () {
          if ($scope.loginLocked) {
              return;
          }

          $scope.loginLocked = true;

          var promise = Auth.login($scope.apiUrl, $scope.username, $scope.password);
          promise.then(function () {
              $scope.loginLocked = false;
              $location.path('/admin/users');
          }, function () {
              $scope.loginLocked = false;
          });
      }
  });
